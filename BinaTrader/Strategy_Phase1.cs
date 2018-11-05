using BinanceExchange.API.Enums;
using BinanceExchange.API.Models.Request;
using BinanceExchange.API.Models.WebSocket;
using BinanceExchange.API.Websockets;
using log4net;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace BinaTrader
{
    class Strategy_Phase1
    {
        internal static class SETTINGS
        {
            public static IReadOnlyList<string> SubSpecificSymbols = new List<string>()
            {
                "XLMUSDT"
            };

            public const int Tiers = 5; // Number of orders placed on EITHER SIDE (buy/sell)
            public const decimal PrimaryMargin = 0.0022M; // +/-(0.25%) each tier
            public static IReadOnlyList<decimal> TierMultipliers = new List<decimal> { 1, 1.5M, 2, 2.5M, 3, 3.5M, 4, 4.3M, 4.6M, 4.9M, 5.2M, 5.5M}; // Margin distance* from center spread
            public const decimal MoveOrderThresh = 0.0008M; // Percent movement of middle spread

            public const decimal PrimaryWager = 12M;  // Amount in USDT
            public const decimal FeePercentage = 0.000M; // 0.1%
            public const decimal TakeProfitMargin = 0.007M; // 0.8% profit

            public static TimeSpan OrderExecDelay = TimeSpan.FromMilliseconds(1200); // Delay between order placements

            public const bool MutePhase2 = false;
        }

        public int BUYProfitCycles { get; private set; }
        public int SELLProfitCycles { get; private set; }
        public readonly BinaDataDepth depthDATA;
        public static BinaDataUser userDATA;
        public List<OrderInfo> OpenBUYorders { get; private set; }
        public List<OrderInfo> OpenSELLorders { get; private set; }
        public Strategy_Phase2 StrategyP2 { get; private set; } 

        private readonly InstanceBinanceWebSocketClient webSocketClient;
        private Guid depthSocketID, userSocketID;
        private static DateTime UserWSConnectTime;
        private static TimeSpan UserWSReconnectTime = TimeSpan.FromMinutes(45);

        private static readonly ILog Log = LogManager.GetLogger(typeof(Strategy_Phase1));

        private const int PriceRoundDigits = 5;
        private const int QtyRoundDigits = 1;



        public Strategy_Phase1()
        {
            userDATA = new BinaDataUser();
            depthDATA = new BinaDataDepth();
            webSocketClient = new InstanceBinanceWebSocketClient(BinaREST.client);
            OpenBUYorders = new List<OrderInfo>();
            OpenSELLorders = new List<OrderInfo>();
            StrategyP2 = new Strategy_Phase2(SETTINGS.SubSpecificSymbols[0], PriceRoundDigits, QtyRoundDigits);
        }


        public async Task Initialize()
        {
            // CONNECT WS (single or combined) AND SEND TO QUEUE:
            if (SETTINGS.SubSpecificSymbols.Count == 1)
                depthSocketID = webSocketClient.ConnectToDepthWebSocket(SETTINGS.SubSpecificSymbols[0], data => depthDATA.DepthUpdateQueue.Enqueue(data));
            else if (SETTINGS.SubSpecificSymbols.Count > 1)
            {
                var symbols = string.Join(",", SETTINGS.SubSpecificSymbols);
                depthSocketID = webSocketClient.ConnectToDepthWebSocketCombined(symbols, data =>
                {
                    depthDATA.DepthUpdateQueue.Enqueue(new BinanceDepthData()
                    {
                        Symbol = data.Stream.Split('@')[0].ToUpper(),
                        UpdateId = data.Data.UpdateId,
                        AskDepthDeltas = data.Data.AskDepthDeltas,
                        BidDepthDeltas = data.Data.BidDepthDeltas
                    });
                });
            }

            // BUILD ORDER BOOK, THEN START DATA UPDATES:
            foreach (string sym in SETTINGS.SubSpecificSymbols)
                await depthDATA.BuildDepthCache(sym);

            depthDATA.StartUpdates();
                        
            //Check and sort currently open orders
            await LoadOpenOrders();

            // Handle UserData WS message events, start UserData WS:
            var userMsgs = new UserDataWebSocketMessages();
            userMsgs.OrderUpdateMessageHandler += data => userDATA.HandleOrderMessage(data);
            userMsgs.TradeUpdateMessageHandler += data => userDATA.HandleTradeMessage(data);
            userMsgs.AccountUpdateMessageHandler += data => userDATA.HandleAccountMessage(data);
            userSocketID = await webSocketClient.ConnectToUserDataWebSocket(userMsgs);
            UserWSConnectTime = DateTime.Now;
        }


        public async Task Execute()
        {
            var spreadMiddle = 0M;
            var lastUpdate = new DateTime();

            // BEGIN EXECUTION LOOP:
            while (true)
            {
                // Place new/Replace tiered orders, then pause
                if (DateTime.Now - lastUpdate > SETTINGS.OrderExecDelay)
                {
                    spreadMiddle = await ReplaceSpreadOrders("XLMUSDT", spreadMiddle);
                    lastUpdate = DateTime.Now;
                }
                else if (userDATA.UserUpdateQueue.IsEmpty)
                    Thread.Sleep(100);                
                

                // PROCESS ORDER UPDATES, MAKE DECISIONS BASED ON ExecutionType:
                while (!userDATA.UserUpdateQueue.IsEmpty)
                {
                    // DeQueue next order update:
                    bool tryDQ = false;
                    var userMsg = new UserDataUpdate();
                    do { tryDQ = userDATA.UserUpdateQueue.TryDequeue(out userMsg); } while (!tryDQ);

                    if (userMsg.TradeOrderData != null) // (!= balance update msg)
                    {
                        var clientID = userMsg.TradeOrderData.NewClientOrderId.Split('_');
                        if (clientID[0] == "bin2")
                        {
                            await StrategyP2.HandleTradeMsg(userMsg.TradeOrderData, clientID);
                            continue;
                        }
                        else if (clientID[0] != "bina")
                            continue;

                        switch (userMsg.TradeOrderData.ExecutionType)
                        {
                            case ExecutionType.New:
                                // UPDATE UNCONFIRMED BUY/SELL OrderInfo in openOrders LIST:
                                var orderInfo = new OrderInfo(userMsg.TradeOrderData);

                                if (clientID[1] == "Buy")
                                    OpenBUYorders[OpenBUYorders.IndexOf(OpenBUYorders.First(o => o.ClientID == userMsg.TradeOrderData.NewClientOrderId))] = orderInfo;
                                else if (clientID[1] == "Sell")
                                    OpenSELLorders[OpenSELLorders.IndexOf(OpenSELLorders.First(o => o.ClientID == userMsg.TradeOrderData.NewClientOrderId))] = orderInfo;

                                break;
                            case ExecutionType.Trade:
                                if (userMsg.TradeOrderData.OrderStatus == OrderStatus.Filled)
                                {
                                    // UPDATE ORDERS StratStatus in OpenDATAorders:
                                    var order = new OrderInfo(userMsg.TradeOrderData);

                                    if (clientID[1] == "Buy")
                                    {
                                        // HANDLE RAPID TP-ORDER HERE 
                                        if (order.StratState == StrategyState.OPEN_FILLED)
                                        {
                                            // place TP order, update to TP_ENTERED
                                            var tpPrice = Math.Round(order.Price * (1 + SETTINGS.TakeProfitMargin), PriceRoundDigits);
                                            var tpQty = order.Qty;
                                            var ord = new CreateOrderRequest()
                                            {
                                                Side = OrderSide.Sell,
                                                Symbol = order.Symbol,
                                                Price = tpPrice,
                                                Quantity = tpQty,
                                                NewClientOrderId = order.ClientID
                                            };
                                            var resp = await BinaREST.CreateLimitOrder(ord);
                                            if (resp != null)
                                                Log.Debug($">>> [BUY] Take-Profit submitted");

                                            // update order info
                                            order = new OrderInfo(ord, StrategyState.TP_UNCONFIRMED);
                                        }

                                        OpenBUYorders[OpenBUYorders.IndexOf(OpenBUYorders.First(o => o.ClientID == userMsg.TradeOrderData.NewClientOrderId))] = order;
                                    }                                        
                                    else if (clientID[1] == "Sell")
                                    {
                                        // HANDLE RAPID TP-ORDER HERE
                                        if (order.StratState == StrategyState.OPEN_FILLED)
                                        {
                                            // place TP order, update to TP_ENTERED
                                            var tpPrice = Math.Round(order.Price * (1 - SETTINGS.TakeProfitMargin), PriceRoundDigits);
                                            var tpQty = Math.Round(Convert.ToDecimal(order.BaseCurrReturn) / (tpPrice * (1 + SETTINGS.FeePercentage)), QtyRoundDigits);
                                            var ord = new CreateOrderRequest()
                                            {
                                                Side = OrderSide.Buy,
                                                Symbol = order.Symbol,
                                                Price = tpPrice,
                                                Quantity = tpQty,
                                                NewClientOrderId = order.ClientID
                                            };
                                            var resp = await BinaREST.CreateLimitOrder(ord);
                                            if (resp != null)
                                                Log.Debug($">>> [SELL] Take-Profit submitted");

                                            // update order info
                                            order = new OrderInfo(ord, StrategyState.TP_UNCONFIRMED);
                                        }

                                        OpenSELLorders[OpenSELLorders.IndexOf(OpenSELLorders.First(o => o.ClientID == userMsg.TradeOrderData.NewClientOrderId))] = order;
                                    }
                                }
                                else if (userMsg.TradeOrderData.OrderStatus == OrderStatus.PartiallyFilled)
                                {
                                    // HANDLE PARTIAL FILLS (ANCHOR UNTIL FILLED)
                                    var order = new OrderInfo(userMsg.TradeOrderData);
                                    if (order.StratState == StrategyState.PARTIAL_FILL)
                                    {
                                        if (clientID[1] == "Buy")
                                            OpenBUYorders[OpenBUYorders.IndexOf(OpenBUYorders.First(o => o.ClientID == userMsg.TradeOrderData.NewClientOrderId))] = order;
                                        else if (clientID[1] == "Sell")
                                            OpenSELLorders[OpenSELLorders.IndexOf(OpenSELLorders.First(o => o.ClientID == userMsg.TradeOrderData.NewClientOrderId))] = order;
                                    }
                                }       

                                break;
                            case ExecutionType.Cancelled:
                                break;
                            case ExecutionType.Rejected:
                                LogOrderMessage(userMsg.TradeOrderData);
                                break;
                            default:
                                LogOrderMessage(userMsg.TradeOrderData);
                                break;

                        }
                    }
                }

                if (OpenBUYorders.Where(o => o.StratState == StrategyState.TP_ENTERED).Count() == SETTINGS.Tiers
                    && OpenSELLorders.Where(o => o.StratState == StrategyState.TP_ENTERED).Count() == SETTINGS.Tiers
                    && !SETTINGS.MutePhase2)
                {
                    var window_top = OpenBUYorders.Min(tp => tp.Price);
                    var window_bottom = OpenSELLorders.Max(tp => tp.Price);
                    await StrategyP2.Activate(depthDATA.GetSpreadMiddle(SETTINGS.SubSpecificSymbols[0]), window_top, window_bottom);
                }
                else
                {
                    await StrategyP2.Deactivate();
                }

            }            
        }
               
        
        private async Task<decimal> ReplaceSpreadOrders(string symbol, decimal oldMiddle)
        {
            // CHECK UserData Websocket STABILITY
            await CheckUserDataWS();

            // SORT LISTS W/ OPEN TAKE-PROFITS AT END
            if (OpenBUYorders.Any())
                OpenBUYorders = SortOrderList(OpenBUYorders);
            if (OpenSELLorders.Any())
                OpenSELLorders = SortOrderList(OpenSELLorders);

            //COPY LIST FOR REVISIONS
            var newBUYorders = new List<OrderInfo>(OpenBUYorders);
            var newSELLorders = new List<OrderInfo>(OpenSELLorders);

            var newMiddle = depthDATA.GetSpreadMiddle(symbol);

            // PLACE/REPLACE ORDERS:
            for (int t = 0; t < SETTINGS.Tiers; t++)
            {
                // BUYS:
                //if (OpenBUYorders[t] == null)
                if (OpenBUYorders.ElementAtOrDefault(t) == null)
                {
                    // no order found on this tier - place order, add new data
                    var buyPrice = Math.Round(newMiddle * (1 - (SETTINGS.PrimaryMargin * SETTINGS.TierMultipliers[t])), PriceRoundDigits);
                    var buyQty = Math.Round(SETTINGS.PrimaryWager / (buyPrice * (1 + SETTINGS.FeePercentage)), QtyRoundDigits);
                    var order = new CreateOrderRequest()
                    {
                        Side = OrderSide.Buy,
                        Symbol = symbol,
                        Price = buyPrice,
                        Quantity = buyQty,
                        NewClientOrderId = "bina_Buy_" + UniqueString()
                    };
                    var response = await BinaREST.CreateLimitOrder(order);
                    if (response != null)
                        Log.Debug($">>> [BUY] INITIAL ORDER PLACED: T{t+1}");

                    // add order status to prevent repeat order
                    newBUYorders.Add(new OrderInfo(order, StrategyState.OPEN_UNCONFIRMED));
                }
                else
                {
                    switch (OpenBUYorders[t].StratState)
                    {
                        case StrategyState.OPEN:
                            if (newMiddle > oldMiddle * (1 + SETTINGS.MoveOrderThresh) || newMiddle < oldMiddle * (1 - SETTINGS.MoveOrderThresh))
                            {
                                // cancel and replace order at new price
                                var cancelResp = await BinaREST.CancelOrder(SETTINGS.SubSpecificSymbols[0], OpenBUYorders[t].ClientID);
                                if (cancelResp != null)
                                    Log.Debug($">>> [BUY] CANCELED: T{t + 1}");
                                var newPrice = Math.Round(newMiddle * (1 - (SETTINGS.PrimaryMargin * SETTINGS.TierMultipliers[t])), PriceRoundDigits);
                                var newQty = Math.Round((OpenBUYorders[t].Qty * (OpenBUYorders[t].Price * (1 + SETTINGS.FeePercentage))) / (newPrice * (1 + SETTINGS.FeePercentage)), QtyRoundDigits);
                                var o = new CreateOrderRequest()
                                {
                                    Side = OrderSide.Buy,
                                    Symbol = symbol,
                                    Price = newPrice,
                                    Quantity = newQty,
                                    NewClientOrderId = OpenBUYorders[t].ClientID
                                };
                                var r = await BinaREST.CreateLimitOrder(o);
                                if (r != null)
                                    Log.Debug($">>> [BUY]    MOVED: T{t + 1}");

                                // update order info
                                newBUYorders[t] = new OrderInfo(o, StrategyState.OPEN_UNCONFIRMED);
                            }                            

                            break;
                        case StrategyState.TP_FILLED:
                            // place new order on starting side, carry over qty for compounding
                            var buyPrice = Math.Round(newMiddle * (1 - (SETTINGS.PrimaryMargin * SETTINGS.TierMultipliers[t])), PriceRoundDigits);
                            var buyQty = Math.Round(Convert.ToDecimal(OpenBUYorders[t].BaseCurrReturn) / (buyPrice * (1 + SETTINGS.FeePercentage)), QtyRoundDigits);
                            var order = new CreateOrderRequest()
                            {
                                Side = OrderSide.Buy,
                                Symbol = symbol,
                                Price = buyPrice,
                                Quantity = buyQty,
                                NewClientOrderId = OpenBUYorders[t].ClientID
                            };
                            var response = await BinaREST.CreateLimitOrder(order);
                            if (response != null)
                                Log.Debug($">>> [BUY] ORDER PLACED: T{t + 1}");

                            // update order info
                            newBUYorders[t] = new OrderInfo(order, StrategyState.OPEN_UNCONFIRMED);
                            BUYProfitCycles++;
                            break;
                    }
                }

                // SELLS:
                if (OpenSELLorders.ElementAtOrDefault(t) == null)
                {
                    // no order found on this tier - place order, add new data
                    var sellPrice = Math.Round(newMiddle * (1 + (SETTINGS.PrimaryMargin * SETTINGS.TierMultipliers[t])), PriceRoundDigits);
                    var sellQty = Math.Round(SETTINGS.PrimaryWager / (sellPrice * (1 - SETTINGS.FeePercentage)), QtyRoundDigits);
                    var order = new CreateOrderRequest()
                    {
                        Side = OrderSide.Sell,
                        Symbol = symbol,
                        Price = sellPrice,
                        Quantity = sellQty,
                        NewClientOrderId = "bina_Sell_" + UniqueString()
                    };
                    var response = await BinaREST.CreateLimitOrder(order);
                    if (response != null)
                        Log.Debug($">>> [SELL] INITIAL ORDER PLACED: T{t + 1}");

                    // add order status to prevent repeat order
                    newSELLorders.Add(new OrderInfo(order, StrategyState.OPEN_UNCONFIRMED));
                }
                else
                {
                    switch (OpenSELLorders[t].StratState)
                    {
                        case StrategyState.OPEN:
                            if (newMiddle > oldMiddle * (1 + SETTINGS.MoveOrderThresh) || newMiddle < oldMiddle * (1 - SETTINGS.MoveOrderThresh))
                            {    
                                // cancel and replace order at new price
                                var cancelResp = await BinaREST.CancelOrder(SETTINGS.SubSpecificSymbols[0], OpenSELLorders[t].ClientID);
                                if (cancelResp != null)
                                    Log.Debug($">>> [SELL] CANCELED: T{t + 1}");
                                var newPrice = Math.Round(newMiddle * (1 + (SETTINGS.PrimaryMargin * SETTINGS.TierMultipliers[t])), PriceRoundDigits);
                                var newQty = Math.Round((OpenSELLorders[t].Qty * (OpenSELLorders[t].Price * (1 - SETTINGS.FeePercentage))) / (newPrice * (1 - SETTINGS.FeePercentage)), QtyRoundDigits);
                                var o = new CreateOrderRequest()
                                {
                                    Side = OrderSide.Sell,
                                    Symbol = symbol,
                                    Price = newPrice,
                                    Quantity = newQty,
                                    NewClientOrderId = OpenSELLorders[t].ClientID
                                };
                                var r = await BinaREST.CreateLimitOrder(o);
                                if (r != null)
                                    Log.Debug($">>> [SELL]    MOVED: T{t + 1}");

                                // update order info
                                newSELLorders[t] = new OrderInfo(o, StrategyState.OPEN_UNCONFIRMED);
                            }                            

                            break;
                        case StrategyState.TP_FILLED:
                            // place new order on starting side, carry over qty for compounding
                            var sellPrice = Math.Round(newMiddle * (1 + (SETTINGS.PrimaryMargin * SETTINGS.TierMultipliers[t])), PriceRoundDigits);
                            var sellQty = OpenSELLorders[t].Qty;
                            var order = new CreateOrderRequest()
                            {
                                Side = OrderSide.Sell,
                                Symbol = symbol,
                                Price = sellPrice,
                                Quantity = sellQty,
                                NewClientOrderId = OpenSELLorders[t].ClientID
                            };
                            var response = await BinaREST.CreateLimitOrder(order);
                            if (response != null)
                                Log.Debug($">>> [SELL] ORDER PLACED: T{t + 1}");

                            // update order info
                            newSELLorders[t] = new OrderInfo(order, StrategyState.OPEN_UNCONFIRMED);
                            SELLProfitCycles++;
                            break;
                    }
                }
            }

            // UPDATE ORDER STATUS:
            OpenBUYorders = new List<OrderInfo>(newBUYorders);
            OpenSELLorders = new List<OrderInfo>(newSELLorders);

            if (newMiddle > oldMiddle * (1 + SETTINGS.MoveOrderThresh) || newMiddle < oldMiddle * (1 - SETTINGS.MoveOrderThresh))
                return newMiddle;
            else
                return oldMiddle;
        }


        private List<OrderInfo> SortOrderList(List<OrderInfo> orders)
        {
            // get unconfirmed orders:
            var unc = orders.Where(o => o.StratState == StrategyState.OPEN_UNCONFIRMED || o.StratState == StrategyState.TP_UNCONFIRMED).ToList();                        

            if (unc.Any())
            {
                // get indexes for unconfirmed tier orders:
                var pendingIndexes = Enumerable.Range(0, orders.Count)
                 .Where(n => orders[n].StratState == StrategyState.OPEN_UNCONFIRMED || orders[n].StratState == StrategyState.TP_UNCONFIRMED).ToList();

                // sort order list:
                orders = orders.OrderBy(ord => ord.StratState).ToList();

                // remove pending orders and replace at their original index/tier:: 
                orders.RemoveAll(o => o.StratState == StrategyState.OPEN_UNCONFIRMED || o.StratState == StrategyState.TP_UNCONFIRMED);

                int i = 0;
                foreach (int index in pendingIndexes)
                {
                    //if (orders.ElementAtOrDefault(index) != null)
                    orders.Insert(index, unc[i]);
                    i++;
                }
            }
            else
                // just sort order list:
                orders = orders.OrderBy(ord => ord.StratState).ToList();

            return orders;
        }


        private async Task CheckUserDataWS()
        {
            // Check for UserData Websocket stability
            if (DateTime.Now - UserWSConnectTime >= UserWSReconnectTime)
            {
                if (!OpenBUYorders.Any(o => o.StratState == StrategyState.OPEN_FILLED || o.StratState == StrategyState.TP_FILLED) ||
                !OpenSELLorders.Any(ord => ord.StratState == StrategyState.OPEN_FILLED || ord.StratState == StrategyState.TP_FILLED))
                {
                    await ReconnectUserDataWS();
                    UserWSConnectTime = DateTime.Now;
                }
            }
        }


        private async Task ReconnectUserDataWS()
        {
            Log.Debug(">>> !!! USER DATA SOCKET RECONNECTING...");
            // Close socket
            webSocketClient.CloseWebSocketInstance(userSocketID);

            // Re-initialize
            userDATA = new BinaDataUser();
            OpenBUYorders = new List<OrderInfo>();
            OpenSELLorders = new List<OrderInfo>();
            StrategyP2 = new Strategy_Phase2(SETTINGS.SubSpecificSymbols[0], PriceRoundDigits, QtyRoundDigits);


            // Check and sort currently open orders
            await LoadOpenOrders();

            // Reassign events and re-open WS:
            var userMsgs = new UserDataWebSocketMessages();
            userMsgs.OrderUpdateMessageHandler += data => userDATA.HandleOrderMessage(data);
            userMsgs.TradeUpdateMessageHandler += data => userDATA.HandleTradeMessage(data);
            userMsgs.AccountUpdateMessageHandler += data => userDATA.HandleAccountMessage(data);
            userSocketID = await webSocketClient.ConnectToUserDataWebSocket(userMsgs);
            Log.Debug(">>> !!! USER DATA SOCKET RECONNECTED");
        }
        

        private async Task LoadOpenOrders()
        {
            // CALL ALL ORDERS FOR MARKET, SORT THROUGH ALREADY PLACED ORDERS
            var orders = await BinaREST.GetOpenOrders(SETTINGS.SubSpecificSymbols[0]);
            foreach (var ord in orders)
            {
                var clientID = ord.ClientOrderId.Split('_');
                if (clientID[0] == "bin2")
                {
                    await StrategyP2.LoadOpenP2Order(ord, clientID);
                    continue;
                }
                else if (clientID[0] != "bina")
                    continue;

                var ordInfo = new OrderInfo(ord);

                if (ordInfo.Status == OrderStatus.New)
                {
                    if (clientID[1] == "Buy")
                        OpenBUYorders.Add(ordInfo);
                    else if (clientID[1] == "Sell")
                        OpenSELLorders.Add(ordInfo);
                    continue;
                }
                else if (ordInfo.Status == OrderStatus.PartiallyFilled)
                {
                    if (clientID[1] == "Buy")
                        if (ordInfo.Side == OrderSide.Buy)
                        {
                            ordInfo.StratState = StrategyState.PARTIAL_FILL;
                            OpenBUYorders.Add(ordInfo);
                        }
                        else
                            OpenBUYorders.Add(ordInfo);
                    else if (clientID[1] == "Sell")
                        if (ordInfo.Side == OrderSide.Sell)
                        {
                            {
                                ordInfo.StratState = StrategyState.PARTIAL_FILL;
                                OpenSELLorders.Add(ordInfo);
                            }
                        }
                        else
                            OpenSELLorders.Add(ordInfo);
                    continue;
                }
            }

        }
        

        private string UniqueString()
        {
            Guid g = Guid.NewGuid();
            string GuidString = Convert.ToBase64String(g.ToByteArray());
            GuidString = GuidString.Replace("=", "");
            GuidString = GuidString.Replace("+", "");
            GuidString = GuidString.Replace("/", "");
            return GuidString;
        }


        private void LogOrderMessage(BinanceTradeOrderData TradeMsg)
        {
            //Console.WriteLine($"{JsonConvert.SerializeObject(userMsg.TradeOrderData, Formatting.Indented)}");
            Log.Debug("\n--------------------------------------------------------------------------\n\n{");
            Log.Debug($"ExecutionType: {TradeMsg.ExecutionType}");
            Log.Debug($"OrderStatus: {TradeMsg.OrderStatus}");
            Log.Debug($"Side: {TradeMsg.Side}");
            Log.Debug($"Symbol: {TradeMsg.Symbol}");
            Log.Debug($"Price: {TradeMsg.Price}");
            Log.Debug($"Quantity: {TradeMsg.Quantity}");
            Log.Debug($"PriceOfLastFilledTrade: {TradeMsg.PriceOfLastFilledTrade}");
            Log.Debug($"QuantityOfLastFilledTrade: {TradeMsg.QuantityOfLastFilledTrade}");
            Log.Debug($"AccumulatedQuantityOfFilledTradesThisOrder: {TradeMsg.AccumulatedQuantityOfFilledTradesThisOrder}");
            Log.Debug($"NewClientOrderId: {TradeMsg.NewClientOrderId}");
            Log.Debug($"OrderRejectReason: {TradeMsg.OrderRejectReason}");
            Log.Debug("}");
        }
                
    }

}