using BinanceExchange.API.Client;
using BinanceExchange.API.Enums;
using BinanceExchange.API.Models.Request;
using BinanceExchange.API.Models.Response;
using BinanceExchange.API.Models.Response.Abstract;
using BinanceExchange.API.Models.Response.Error;
using log4net;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace BinaTrader
{
    public static class BinaREST
    {
        public static BinanceClient client;
        private static readonly bool useCache = false;

        static readonly ILog Log = LogManager.GetLogger(typeof(BinaREST));

        public static void SetClient(BinanceClient c)
        {
            client = c;
        }
        
        public static async Task<OrderBookResponse> GetOrderBook(string symbol, Int32 deep)
        {
            var depthResults = await client.GetOrderBook(symbol, useCache, deep);
            return depthResults;
        }

        public static async Task<BaseCreateOrderResponse> CreateLimitOrder(CreateOrderRequest request)
        {
            request.Type = OrderType.Limit;
            request.TimeInForce = TimeInForce.GTC;
            try { return await client.CreateOrder(request); }

            catch (BinanceBadRequestException badRequestException) { Log.Error(badRequestException.Message); return null; }
            catch (BinanceServerException serverException) { Log.Error(serverException.Message); return null; }
            catch (BinanceTimeoutException timeoutException) { Log.Error(timeoutException.Message); return null; }
            catch (BinanceException unknownException) { Log.Error(unknownException); return null; }
        }

        public static async Task<CancelOrderResponse> CancelOrder(string delta, string ClientID = null, long OrderID = -1)
        {
            var request = new CancelOrderRequest();
            request.Symbol = delta;
            if (ClientID != null)
                request.OriginalClientOrderId = ClientID;
            else if (OrderID != -1)
                request.OrderId = OrderID;
            else
                return null;

            try { return await client.CancelOrder(request); }

            catch (BinanceBadRequestException badRequestException) { Log.Error(badRequestException.Message); return null; }
            catch (BinanceServerException serverException) { Log.Error(serverException.Message); return null; }
            catch (BinanceTimeoutException timeoutException) { Log.Error(timeoutException.Message); return null; }
            catch (BinanceException unknownException) { Log.Error(unknownException); return null; }
        }

        public static async Task<List<OrderResponse>> GetOpenOrders(string delta)
        {
            try { return await client.GetCurrentOpenOrders(new CurrentOpenOrdersRequest() { Symbol = delta }); }

            catch (BinanceBadRequestException badRequestException) { Log.Error(badRequestException.Message); return null; }
            catch (BinanceServerException serverException) { Log.Error(serverException.Message); return null; }
            catch (BinanceTimeoutException timeoutException) { Log.Error(timeoutException.Message); return null; }
            catch (BinanceException unknownException) { Log.Error(unknownException); return null; }
        }
        public static async Task<ExchangeInfoResponse> GetAssetUnits()
        {
            try { return await client.GetExchangeInfo(); }

            catch (BinanceBadRequestException badRequestException) { Log.Error(badRequestException.Message); return null; }
            catch (BinanceServerException serverException) { Log.Error(serverException.Message); return null; }
            catch (BinanceTimeoutException timeoutException) { Log.Error(timeoutException.Message); return null; }
            catch (BinanceException unknownException) { Log.Error(unknownException); return null; }
        }

    }
}
