using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Client;
using BTCPayServer.Client.Models;
using BTCPayServer.Configuration;
using BTCPayServer.Data;
using BTCPayServer.Data.Payouts.LightningLike;
using BTCPayServer.HostedServices;
using BTCPayServer.Lightning;
using BTCPayServer.Payments;
using BTCPayServer.Payments.Bitcoin;
using BTCPayServer.Payments.Lightning;
using BTCPayServer.Payouts;
using BTCPayServer.Services;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Stores;
using LNURL;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NBitcoin;
using Newtonsoft.Json.Linq;
using static BTCPayServer.Data.Payouts.LightningLike.UILightningLikePayoutController;
using MarkPayoutRequest = BTCPayServer.HostedServices.MarkPayoutRequest;
using PayoutData = BTCPayServer.Data.PayoutData;
using PayoutProcessorData = BTCPayServer.Data.PayoutProcessorData;

namespace BTCPayServer.PayoutProcessors.Lightning;

public class LightningAutomatedPayoutProcessor : BaseAutomatedPayoutProcessor<LightningAutomatedPayoutBlob>
{
	private readonly BTCPayNetworkJsonSerializerSettings _btcPayNetworkJsonSerializerSettings;
	private readonly LightningClientFactoryService _lightningClientFactoryService;
	private readonly UserService _userService;
	private readonly IOptions<LightningNetworkOptions> _options;
	private readonly PullPaymentHostedService _pullPaymentHostedService;
	private readonly LightningLikePayoutHandler _payoutHandler;
	public BTCPayNetwork Network => _payoutHandler.Network;
	private readonly PaymentMethodHandlerDictionary _handlers;

	public LightningAutomatedPayoutProcessor(
		PayoutMethodId payoutMethodId,
		BTCPayNetworkJsonSerializerSettings btcPayNetworkJsonSerializerSettings,
		LightningClientFactoryService lightningClientFactoryService,
		PayoutMethodHandlerDictionary payoutHandlers,
		UserService userService,
		ILoggerFactory logger, IOptions<LightningNetworkOptions> options,
		StoreRepository storeRepository, PayoutProcessorData payoutProcessorSettings,
		ApplicationDbContextFactory applicationDbContextFactory,
		PaymentMethodHandlerDictionary handlers,
		IPluginHookService pluginHookService,
		EventAggregator eventAggregator,
		PullPaymentHostedService pullPaymentHostedService) :
		base(PaymentTypes.LN.GetPaymentMethodId(GetPayoutHandler(payoutHandlers, payoutMethodId).Network.CryptoCode), logger, storeRepository, payoutProcessorSettings, applicationDbContextFactory,
			handlers, pluginHookService, eventAggregator)
	{
		_btcPayNetworkJsonSerializerSettings = btcPayNetworkJsonSerializerSettings;
		_lightningClientFactoryService = lightningClientFactoryService;
		_userService = userService;
		_options = options;
		_pullPaymentHostedService = pullPaymentHostedService;
		_payoutHandler = GetPayoutHandler(payoutHandlers, payoutMethodId);
		_handlers = handlers;
	}
	private static LightningLikePayoutHandler GetPayoutHandler(PayoutMethodHandlerDictionary payoutHandlers, PayoutMethodId payoutMethodId)
	{
		return (LightningLikePayoutHandler)payoutHandlers[payoutMethodId];
	}

    public async Task<ResultVM> HandlePayout(PayoutData payoutData, ILightningClient lightningClient, CancellationToken cancellationToken)
	{
        using var scope = _payoutHandler.PayoutsPaymentProcessing.StartTracking();
		if (payoutData.State != PayoutState.AwaitingPayment || !scope.TryTrack(payoutData.Id))
			return InvalidState(payoutData.Id);
        var blob = payoutData.GetBlob(_btcPayNetworkJsonSerializerSettings);
		var res = await _pullPaymentHostedService.MarkPaid(new MarkPayoutRequest()
		{
			State = PayoutState.InProgress,
			PayoutId = payoutData.Id,
			Proof = null
		});
		if (res != MarkPayoutRequest.PayoutPaidResult.Ok)
            return InvalidState(payoutData.Id);
        ResultVM result;
        var claim = await _payoutHandler.ParseClaimDestination(blob.Destination, cancellationToken);
        switch (claim.destination)
        {
            case LNURLPayClaimDestinaton lnurlPayClaimDestinaton:
                var lnurlResult = await GetInvoiceFromLNURL(payoutData, _payoutHandler, blob,
                    lnurlPayClaimDestinaton, cancellationToken);
                if (lnurlResult.Item2 is not null)
                {
                    result = lnurlResult.Item2;
                }
                else
                {
                    result = await TrypayBolt(lightningClient, blob, payoutData, lnurlResult.Item1, cancellationToken);
                }
                break;

            case BoltInvoiceClaimDestination item1:
                result = await TrypayBolt(lightningClient, blob, payoutData, item1.PaymentRequest, cancellationToken);
                break;
            default:
                result = new ResultVM
                {
                    PayoutId = payoutData.Id,
                    Result = PayResult.Error,
                    Destination = blob.Destination,
                    Message = claim.error
                };
                break;
        }

        bool updateBlob = false;
		if (result.Result is PayResult.Error or PayResult.CouldNotFindRoute && payoutData.State == PayoutState.AwaitingPayment)
		{
			var errorCount = IncrementErrorCount(blob);
			updateBlob = true;
			if (errorCount >= 10)
				payoutData.State = PayoutState.Cancelled;
		}
		if (payoutData.State != PayoutState.InProgress || payoutData.Proof is not null)
		{
			await _pullPaymentHostedService.MarkPaid(new MarkPayoutRequest()
			{
				State = payoutData.State,
				PayoutId = payoutData.Id,
				Proof = payoutData.GetProofBlobJson(),
				UpdateBlob = updateBlob ? blob : null
			});
		}
        return result;
	}

    private ResultVM InvalidState(string payoutId) =>
        new ResultVM
        {
            PayoutId = payoutId,
            Result = PayResult.Error,
            Message = "The payout isn't in a valid state"
        };

    private int IncrementErrorCount(PayoutBlob blob)
    {
		int count;
		if (blob.AdditionalData.TryGetValue("ErrorCount", out var v) && v.Type == JTokenType.Integer)
		{
			count = v.Value<int>() + 1;
			blob.AdditionalData["ErrorCount"] = count;
		}
		else
		{
			count = 1;
			blob.AdditionalData.Add("ErrorCount", count);
		}
		return count;
    }

    async Task<(BOLT11PaymentRequest, ResultVM)> GetInvoiceFromLNURL(PayoutData payoutData,
            LightningLikePayoutHandler handler, PayoutBlob blob, LNURLPayClaimDestinaton lnurlPayClaimDestinaton, CancellationToken cancellationToken)
    {
        var endpoint = lnurlPayClaimDestinaton.LNURL.IsValidEmail()
            ? LNURL.LNURL.ExtractUriFromInternetIdentifier(lnurlPayClaimDestinaton.LNURL)
            : LNURL.LNURL.Parse(lnurlPayClaimDestinaton.LNURL, out _);
        var httpClient = handler.CreateClient(endpoint);
        var lnurlInfo =
            (LNURLPayRequest)await LNURL.LNURL.FetchInformation(endpoint, "payRequest",
                httpClient, cancellationToken);
        var lm = new LightMoney(payoutData.Amount.Value, LightMoneyUnit.BTC);
        if (lm > lnurlInfo.MaxSendable || lm < lnurlInfo.MinSendable)
        {

            payoutData.State = PayoutState.Cancelled;
            return (null, new ResultVM
            {
                PayoutId = payoutData.Id,
                Result = PayResult.Error,
                Destination = blob.Destination,
                Message =
                    $"The LNURL provided would not generate an invoice of {lm.ToDecimal(LightMoneyUnit.Satoshi)} sats"
            });
        }

        try
        {
            var lnurlPayRequestCallbackResponse =
                await lnurlInfo.SendRequest(lm, this.Network.NBitcoinNetwork, httpClient, cancellationToken: cancellationToken);

            return (lnurlPayRequestCallbackResponse.GetPaymentRequest(this.Network.NBitcoinNetwork), null);
        }
        catch (LNUrlException e)
        {
            return (null,
                new ResultVM
                {
                    PayoutId = payoutData.Id,
                    Result = PayResult.Error,
                    Destination = blob.Destination,
                    Message = e.Message
                });
        }
    }

    async Task<ResultVM> TrypayBolt(
            ILightningClient lightningClient, PayoutBlob payoutBlob, PayoutData payoutData, BOLT11PaymentRequest bolt11PaymentRequest, CancellationToken cancellationToken)
    {
        var boltAmount = bolt11PaymentRequest.MinimumAmount.ToDecimal(LightMoneyUnit.BTC);

        // BoltAmount == 0: Any amount is OK.
        // While we could allow paying more than the minimum amount from the boltAmount,
        // Core-Lightning do not support it! It would just refuse to pay more than the boltAmount.
        if (boltAmount != payoutData.Amount.Value && boltAmount != 0.0m)
        {
            payoutData.State = PayoutState.Cancelled;
            return new ResultVM
            {
                PayoutId = payoutData.Id,
                Result = PayResult.Error,
                Message = $"The BOLT11 invoice amount ({boltAmount} {payoutData.Currency}) did not match the payout's amount ({payoutData.Amount.GetValueOrDefault()} {payoutData.Currency})",
                Destination = payoutBlob.Destination
            };
        }

        if (bolt11PaymentRequest.ExpiryDate < DateTimeOffset.Now)
        {
            payoutData.State = PayoutState.Cancelled;
            return new ResultVM
            {
                PayoutId = payoutData.Id,
                Result = PayResult.Error,
                Message = $"The BOLT11 invoice expiry date ({bolt11PaymentRequest.ExpiryDate}) has expired",
                Destination = payoutBlob.Destination
            };
        }

        var proofBlob = new PayoutLightningBlob { PaymentHash = bolt11PaymentRequest.PaymentHash.ToString() };
        PayResponse pay = null;
        try
        {
            Exception exception = null;
            try
            {
                pay = await lightningClient.Pay(bolt11PaymentRequest.ToString(),
                    new PayInvoiceParams()
                    {
                        Amount = new LightMoney((decimal)payoutData.Amount, LightMoneyUnit.BTC)
                    }, cancellationToken);

                if (pay?.Result is PayResult.CouldNotFindRoute)
                {
                    // Payment failed for sure... we can try again later!
                    payoutData.State = PayoutState.AwaitingPayment;
                    return new ResultVM
                    {
                        PayoutId = payoutData.Id,
                        Result = PayResult.CouldNotFindRoute,
                        Message = $"Unable to find a route for the payment, check your channel liquidity",
                        Destination = payoutBlob.Destination
                    };
                }
            }
            catch (Exception ex)
            {
                exception = ex;
            }

            LightningPayment payment = null;
            try
            {
                payment = await lightningClient.GetPayment(bolt11PaymentRequest.PaymentHash.ToString(), cancellationToken);
            }
            catch (Exception ex)
            {
                exception = ex;
            }
            if (payment is null)
            {
                payoutData.State = PayoutState.Cancelled;
                var exceptionMessage = "";
                if (exception is not null)
                    exceptionMessage = $" ({exception.Message})";
				if (exceptionMessage == "")
					exceptionMessage = $" ({pay?.ErrorDetail})";
				return new ResultVM
                {
                    PayoutId = payoutData.Id,
                    Result = PayResult.Error,
                    Message = $"Unable to confirm the payment of the invoice" + exceptionMessage,
                    Destination = payoutBlob.Destination
                };
            }
            if (payment.Preimage is not null)
                proofBlob.Preimage = payment.Preimage;

            if (payment.Status == LightningPaymentStatus.Complete)
            {
                payoutData.State = PayoutState.Completed;
                payoutData.SetProofBlob(proofBlob, null);
                return new ResultVM
                {
                    PayoutId = payoutData.Id,
                    Result = PayResult.Ok,
                    Destination = payoutBlob.Destination,
                    Message = payment.AmountSent != null
                        ? $"Paid out {payment.AmountSent.ToDecimal(LightMoneyUnit.BTC)} {payoutData.Currency}"
                        : "Paid out"
                };
            }
            else if (payment.Status == LightningPaymentStatus.Failed)
            {
                payoutData.State = PayoutState.AwaitingPayment;
                string reason = "";
                if (pay?.ErrorDetail is string err)
                    reason = $" ({err})";
                return new ResultVM
                {
                    PayoutId = payoutData.Id,
                    Result = PayResult.Error,
                    Destination = payoutBlob.Destination,
                    Message = $"The payment failed{reason}"
                };
            }
            else
            {
                payoutData.State = PayoutState.InProgress;
                return new ResultVM
                {
                    PayoutId = payoutData.Id,
                    Result = PayResult.Unknown,
                    Destination = payoutBlob.Destination,
                    Message = "The payment has been initiated but is still in-flight."
                };
            }
        }
        catch (OperationCanceledException)
        {
            // Timeout, potentially caused by hold invoices
            // Payment will be saved as pending, the LightningPendingPayoutListener will handle settling/cancelling
            payoutData.State = PayoutState.InProgress;
            payoutData.SetProofBlob(proofBlob, null);
            return new ResultVM
            {
                PayoutId = payoutData.Id,
                Result = PayResult.Ok,
                Destination = payoutBlob.Destination,
                Message = "The payment timed out. We will verify if it completed later."
            };
        }
    }

    protected override async Task<bool> ProcessShouldSave(object paymentMethodConfig, List<PayoutData> payouts)
	{
		var lightningSupportedPaymentMethod = (LightningPaymentMethodConfig)paymentMethodConfig;
		if (lightningSupportedPaymentMethod.IsInternalNode &&
			!await _storeRepository.InternalNodePayoutAuthorized(PayoutProcessorSettings.StoreId))
		{
			return false;
		}

		var client =
			lightningSupportedPaymentMethod.CreateLightningClient(Network, _options.Value,
				_lightningClientFactoryService);
		await Task.WhenAll(payouts.Select(data => HandlePayout(data, client, CancellationToken)));

		//we return false because this processor handles db updates on its own
		return false;
	}
}
