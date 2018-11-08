using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using BinanceExchange.API.Enums;
using BinanceExchange.API.Models.Request;
using BinanceExchange.API.Models.Response;
using BinanceExchange.API.Models.WebSocket;
using BinanceExchange.API.Websockets;
using log4net;


namespace BinaTrader
{
    class Strategy_Phase2
    {
        internal static class SETTINGS
        {
            public const decimal grid_interval = 0.005M; // half-percent intervals
            public const int MaxOrdsPerSide = 5; // Max Number of orders placed on EITHER SIDE (buy/sell)
            public const decimal TakeProfitMargin = 0.0075M; // 0.75% profit
            public const decimal PrimaryWager = 14M;  // Amount in USDT
            public const decimal FeePercentage = 0.000M; // 0.1% if not paid in BNB
        }

        private string MarketSymbol;
        private bool P2_Active;
        private List<decimal> grid_values;

        public List<OrderInfo> P2_BuyOrders { get; private set; }
        public List<OrderInfo> P2_SellOrders { get; private set; }
        public int BUYProfitCyclesP2 { get; private set; }
        public int SELLProfitCyclesP2 { get; private set; }

        private readonly int PriceRoundDigits;
        private readonly int QtyRoundDigits;

        private static readonly ILog Log = LogManager.GetLogger(typeof(Strategy_Phase2));


        public Strategy_Phase2(string symbol, int PriceDigs, int QtyDigs)
        {
            MarketSymbol = symbol;
            grid_values = new List<decimal>();
            P2_BuyOrders = new List<OrderInfo>();
            P2_SellOrders = new List<OrderInfo>();
            PriceRoundDigits = PriceDigs;
            QtyRoundDigits = QtyDigs;
            P2_Active = false;
        }

        public async Task LoadOpenP2Order(OrderResponse order, string[] ID)
        {
            // Place OrderP2 object into dictionary
            var ordInfo = new OrderInfo(order);

            if (ordInfo.Status == OrderStatus.New)
            {
                if (ordInfo.StratState == StrategyState.OPEN)
                {
                    var cancelResp = await BinaREST.CancelOrder(ordInfo.Symbol, ordInfo.ClientID);
                    if (cancelResp != null)
                        Log.Debug($">>> [P2_BUY] CANCELED - Price: {ordInfo.Price}");
                }
                else if (ID[1] == "Buy")
                    P2_BuyOrders.Add(ordInfo);
                else if (ID[1] == "Sell")
                    P2_SellOrders.Add(ordInfo);                
            }
            else if (ordInfo.Status == OrderStatus.PartiallyFilled)
            {
                if (ordInfo.StratState == StrategyState.OPEN)
                    ordInfo.StratState = StrategyState.PARTIAL_FILL;
                
                if (ID[1] == "Buy")
                    P2_BuyOrders.Add(ordInfo);
                else if (ID[1] == "Sell")
                    P2_SellOrders.Add(ordInfo);
            }
        }

        public async Task Activate(decimal middle, decimal top, decimal bottom)
        {
            // populate grid values if necessary
            PopulateGridVals(bottom);

            // snap existing P2 TakeProfit orders to an initial grid price
            SetInitialPrices();

            // narrow down grid_values to only possible order placements
            var middleRound = Math.Round(middle, PriceRoundDigits);
            var possibleBuyVals = grid_values.Where(x => x >= middle * (1 - (SETTINGS.grid_interval * SETTINGS.MaxOrdsPerSide))
                                                    && x < middleRound// Within range, and not impeding the phase1 TP boundary
                                                    && x > bottom
                                                    && x * (1 + SETTINGS.TakeProfitMargin) < top).ToList();

            var possibleSellVals = grid_values.Where(x => x <= middle * (1 + (SETTINGS.grid_interval * SETTINGS.MaxOrdsPerSide)) 
                                                    && x > middleRound
                                                    && x < top
                                                    && x * (1 - SETTINGS.TakeProfitMargin) > bottom).ToList();

            // further filter possibleVals and cancel any OPEN orders that fall outside:
            var canceledOrds = new List<OrderInfo>();
            foreach (var order in P2_BuyOrders)
            {
                // remove already placed orders from possibleVals
                if (order.StratState == StrategyState.OPEN 
                    || order.StratState == StrategyState.OPEN_UNCONFIRMED 
                    || order.StratState == StrategyState.OPEN_FILLED 
                    || order.StratState == StrategyState.PARTIAL_FILL)
                    possibleBuyVals.Remove(order.Price);
                // cancel any OPEN status orders outside the possibleVals                
                else if (order.StratState == StrategyState.OPEN && !possibleBuyVals.Any(x => x == order.Price))
                {
                    var cancelResp = await BinaREST.CancelOrder(order.Symbol, order.ClientID);
                    if (cancelResp != null)
                        Log.Debug($">>> [P2_BUY] CANCELED - Price: {order.Price}");
                    canceledOrds.Add(order);
                }
                // and remove possibleVals that already have corresponding TakeProfit.InitPrice open
                else if (order.StratState == StrategyState.TP_ENTERED 
                    || order.StratState == StrategyState.TP_UNCONFIRMED 
                    || order.StratState == StrategyState.TP_FILLED)
                    possibleBuyVals.Remove(order.InitialPrice);
            }
            P2_BuyOrders = P2_BuyOrders.Except(canceledOrds).ToList();

            // Same as above for Sell side:
            canceledOrds.Clear();
            foreach (var order in P2_SellOrders)
            {                
                if (order.StratState == StrategyState.OPEN 
                    || order.StratState == StrategyState.OPEN_UNCONFIRMED 
                    || order.StratState == StrategyState.OPEN_FILLED 
                    || order.StratState == StrategyState.PARTIAL_FILL)
                    possibleSellVals.Remove(order.Price);
                else if (order.StratState == StrategyState.OPEN && !possibleSellVals.Any(x => x == order.Price))
                {
                    var cancelResp = await BinaREST.CancelOrder(order.Symbol, order.ClientID);
                    if (cancelResp != null)
                        Log.Debug($">>> [P2_SELL] CANCELED - Price: {order.Price}");
                    canceledOrds.Add(order);
                }
                else if (order.StratState == StrategyState.TP_ENTERED 
                    || order.StratState == StrategyState.TP_UNCONFIRMED 
                    || order.StratState == StrategyState.TP_FILLED)           
                    possibleSellVals.Remove(order.InitialPrice);   
            }
            P2_SellOrders = P2_SellOrders.Except(canceledOrds).ToList();
            
            possibleBuyVals = possibleBuyVals.Distinct().ToList();
            possibleSellVals = possibleSellVals.Distinct().ToList();

            // place orders on remaining values, add to orderInfo Lists
            possibleBuyVals.OrderByDescending(v => v);
            foreach (var p in possibleBuyVals)
            {
                // FIRST REPLACE ANY 'TP_FILLED' WITH COMPOUNDED GAINS
                var filledTP = P2_BuyOrders.FirstOrDefault(o => o.StratState == StrategyState.TP_FILLED);
                if (filledTP != null)
                {
                    var buyQty = Math.Round(Convert.ToDecimal(filledTP.BaseCurrReturn) / (p * (1 + SETTINGS.FeePercentage)), QtyRoundDigits);
                    var order = new CreateOrderRequest()
                    {
                        Side = OrderSide.Buy,
                        Symbol = MarketSymbol,
                        Price = p,
                        Quantity = buyQty,
                        NewClientOrderId = filledTP.ClientID
                    };
                    var response = await BinaREST.CreateLimitOrder(order);
                    if (response != null)
                        Log.Debug($">>> [BUY] (RE)ORDER PLACED - Price: {p}");

                    // update order info
                    var newOrd = new OrderInfo(order, StrategyState.OPEN_UNCONFIRMED);
                    newOrd.InitialPrice = p;
                    P2_BuyOrders[P2_BuyOrders.IndexOf(P2_BuyOrders.First(o => o.ClientID == filledTP.ClientID))] = newOrd;
                }
                //Place remaining available OPEN order slots
                else if (P2_BuyOrders.Count() < SETTINGS.MaxOrdsPerSide)
                {
                    var buyQty = Math.Round(SETTINGS.PrimaryWager / (p * (1 + SETTINGS.FeePercentage)), QtyRoundDigits);
                    var order = new CreateOrderRequest()
                    {
                        Side = OrderSide.Buy,
                        Symbol = MarketSymbol,
                        Price = p,
                        Quantity = buyQty,
                        NewClientOrderId = "bin2_Buy_" + UniqueString()
                    };
                    var response = await BinaREST.CreateLimitOrder(order);
                    if (response != null)
                        Log.Debug($">>> [P2_BUY] ORDER PLACED - Price: {p}");

                    // add order status to prevent repeat order
                    var newOrd = new OrderInfo(order, StrategyState.OPEN_UNCONFIRMED);
                    newOrd.InitialPrice = p;
                    P2_BuyOrders.Add(newOrd);
                }
                // MOVE UP LOWER BUY ORDERS 
                else if (P2_BuyOrders.Any(o => o.StratState == StrategyState.OPEN && o.Price < p))
                {
                    // Cancel lowest buy order
                    var minPrice = P2_BuyOrders.Where(o => o.StratState == StrategyState.OPEN).Min(o => o.Price);
                    var oldOrder = P2_BuyOrders.First(o => o.Price == minPrice);

                    var baseReturn = 0M;
                    if (oldOrder.BaseCurrReturn != null)
                        baseReturn = Convert.ToDecimal(oldOrder.BaseCurrReturn);

                    var cancelResp = await BinaREST.CancelOrder(oldOrder.Symbol, oldOrder.ClientID);
                    if (cancelResp != null)
                        Log.Debug($">>> [P2_SELL] CANCELED - Price: {oldOrder.Price}");
                    P2_BuyOrders.Remove(oldOrder);

                    // Place order at correct price
                    // If baseCurr != 0, use this to calc QTY, else primary wager
                    var buyQty = Math.Round(SETTINGS.PrimaryWager / (p * (1 + SETTINGS.FeePercentage)), QtyRoundDigits);
                    if (baseReturn != 0)
                        buyQty = Math.Round(Convert.ToDecimal(baseReturn) / (p * (1 + SETTINGS.FeePercentage)), QtyRoundDigits);

                    var order = new CreateOrderRequest()
                    {
                        Side = OrderSide.Buy,
                        Symbol = MarketSymbol,
                        Price = p,
                        Quantity = buyQty,
                        NewClientOrderId = "bin2_Buy_" + UniqueString()
                    };
                    var response = await BinaREST.CreateLimitOrder(order);
                    if (response != null)
                        Log.Debug($">>> [P2_BUY] ORDER PLACED - Price: {p}");

                    // add order status to prevent repeat order
                    var newOrd = new OrderInfo(order, StrategyState.OPEN_UNCONFIRMED);
                    newOrd.InitialPrice = p;
                    P2_BuyOrders.Add(newOrd);
                }
            }

            // Same as above for sell side:
            possibleSellVals.OrderBy(v => v);
            foreach (var p in possibleSellVals)
            {
                // FIRST REPLACE ANY 'TP_FILLED' WITH COMPOUNDED GAINS
                var filledTP = P2_SellOrders.FirstOrDefault(o => o.StratState == StrategyState.TP_FILLED);
                if (filledTP != null)
                {
                    var sellQty = filledTP.Qty;
                    var order = new CreateOrderRequest()
                    {
                        Side = OrderSide.Sell,
                        Symbol = MarketSymbol,
                        Price = p,
                        Quantity = sellQty,
                        NewClientOrderId = filledTP.ClientID
                    };
                    var response = await BinaREST.CreateLimitOrder(order);
                    if (response != null)
                        Log.Debug($">>> [SELL] ORDER (RE)PLACED - Price: {p}");

                    // update order info
                    var newOrd = new OrderInfo(order, StrategyState.OPEN_UNCONFIRMED);
                    newOrd.InitialPrice = p;
                    P2_SellOrders[P2_SellOrders.IndexOf(P2_SellOrders.First(o => o.ClientID == filledTP.ClientID))] = newOrd;
                }
                //Place remaining available OPEN order slots
                else if (P2_SellOrders.Count() < SETTINGS.MaxOrdsPerSide)
                {
                    var sellQty = Math.Round(SETTINGS.PrimaryWager / (p * (1 - SETTINGS.FeePercentage)), QtyRoundDigits);
                    var order = new CreateOrderRequest()
                    {
                        Side = OrderSide.Sell,
                        Symbol = MarketSymbol,
                        Price = p,
                        Quantity = sellQty,
                        NewClientOrderId = "bin2_Sell_" + UniqueString()
                    };
                    var response = await BinaREST.CreateLimitOrder(order);
                    if (response != null)
                        Log.Debug($">>> [P2_SELL] ORDER PLACED - Price: {p}");

                    // add order status to prevent repeat order
                    var newOrd = new OrderInfo(order, StrategyState.OPEN_UNCONFIRMED);
                    newOrd.InitialPrice = p;
                    P2_SellOrders.Add(newOrd);
                }
                // MOVE DOWN ANY HIGHER SELL ORDERS 
                else if (P2_SellOrders.Any(o => o.StratState == StrategyState.OPEN && o.Price > p))
                {
                    // Cancel order
                    var maxPrice = P2_SellOrders.Where(o => o.StratState == StrategyState.OPEN).Max(o => o.Price);
                    var oldOrder = P2_SellOrders.First(o => o.Price == maxPrice);

                    var baseCurr = (oldOrder.Price * (1 - SETTINGS.FeePercentage)) * oldOrder.Qty; 

                    var cancelResp = await BinaREST.CancelOrder(oldOrder.Symbol, oldOrder.ClientID);
                    if (cancelResp != null)
                        Log.Debug($">>> [P2_SELL] CANCELED - Price: {oldOrder.Price}");
                    P2_SellOrders.Remove(oldOrder);

                    // Place order at correct price
                    // use same BaseCurr wager amount 
                    var sellQty = Math.Round(baseCurr / (p * (1 - SETTINGS.FeePercentage)), QtyRoundDigits);
                    var order = new CreateOrderRequest()
                    {
                        Side = OrderSide.Sell,
                        Symbol = MarketSymbol,
                        Price = p,
                        Quantity = sellQty,
                        NewClientOrderId = "bin2_Sell_" + UniqueString()
                    };
                    var response = await BinaREST.CreateLimitOrder(order);
                    if (response != null)
                        Log.Debug($">>> [P2_SELL] ORDER PLACED - Price: {p}");

                    // add order status to prevent repeat order
                    var newOrd = new OrderInfo(order, StrategyState.OPEN_UNCONFIRMED);
                    newOrd.InitialPrice = p;
                    P2_SellOrders.Add(newOrd);
                }
            }

            P2_Active = true;
        }


        public async Task Deactivate()
        {
            if (!P2_Active)
                return;

            // Cancel all OPEN orders, leave TakeProfits open
            var canceledOrds = new List<OrderInfo>();
            foreach (var ord in P2_BuyOrders.Where(o => o.StratState == StrategyState.OPEN))
            {
                var cancelResp = await BinaREST.CancelOrder(ord.Symbol, ord.ClientID);
                if (cancelResp != null)
                    Log.Debug($">>> [P2_BUY] CANCELED - Price: {ord.Price}");

                canceledOrds.Add(ord);
            }
            P2_BuyOrders = P2_BuyOrders.Except(canceledOrds).ToList();

            canceledOrds.Clear();
            foreach (var ord in P2_SellOrders.Where(o => o.StratState == StrategyState.OPEN))
            {
                var cancelResp = await BinaREST.CancelOrder(ord.Symbol, ord.ClientID);
                if (cancelResp != null)
                    Log.Debug($">>> [P2_BUY] CANCELED - Price: {ord.Price}");

                canceledOrds.Add(ord);
            }
            P2_SellOrders = P2_SellOrders.Except(canceledOrds).ToList();

            P2_Active = false;
        }


        public async Task HandleTradeMsg(BinanceTradeOrderData TradeMsg, string[] ID)
        {
            // EXECUTION SWITCH
            switch (TradeMsg.ExecutionType)
            {
                case ExecutionType.New:
                    // UPDATE UNCONFIRMED BUY/SELL OrderInfo in openOrders LIST:
                    var orderInfo = new OrderInfo(TradeMsg);

                    if (ID[1] == "Buy")
                        P2_BuyOrders[P2_BuyOrders.IndexOf(P2_BuyOrders.First(o => o.ClientID == TradeMsg.NewClientOrderId))] = orderInfo;
                    else if (ID[1] == "Sell")
                        P2_SellOrders[P2_SellOrders.IndexOf(P2_SellOrders.First(o => o.ClientID == TradeMsg.NewClientOrderId))] = orderInfo;

                    break;
                case ExecutionType.Trade:
                    if (TradeMsg.OrderStatus == OrderStatus.Filled)
                    {
                        // UPDATE ORDERS StratStatus in OpenDATAorders:
                        var order = new OrderInfo(TradeMsg);

                        if (ID[1] == "Buy")
                        {
                            // HANDLE RAPID TP-ORDER HERE 
                            if (order.StratState == StrategyState.OPEN_FILLED)
                            {
                                // place TP order, update to TP_ENTERED
                                var tpPrice = Math.Round(order.Price * (1 + SETTINGS.TakeProfitMargin), PriceRoundDigits);
                                var tpQty = order.Qty;
                                var tpOrd = new CreateOrderRequest()
                                {
                                    Side = OrderSide.Sell,
                                    Symbol = order.Symbol,
                                    Price = tpPrice,
                                    Quantity = tpQty,
                                    NewClientOrderId = order.ClientID
                                };
                                var resp = await BinaREST.CreateLimitOrder(tpOrd);
                                if (resp != null)
                                    Log.Debug($">>> [BUY] Take-Profit submitted");

                                // update order info
                                var initPrice = order.Price;
                                order = new OrderInfo(tpOrd, StrategyState.TP_UNCONFIRMED);
                                order.InitialPrice = initPrice;
                            }
                            else if (order.StratState == StrategyState.TP_FILLED)
                                BUYProfitCyclesP2++;

                            P2_BuyOrders[P2_BuyOrders.IndexOf(P2_BuyOrders.First(o => o.ClientID == TradeMsg.NewClientOrderId))] = order;
                        }
                        else if (ID[1] == "Sell")
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
                                var initPrice = order.Price;
                                order = new OrderInfo(ord, StrategyState.TP_UNCONFIRMED);
                                order.InitialPrice = initPrice;
                            }
                            else if (order.StratState == StrategyState.TP_FILLED)
                                SELLProfitCyclesP2++;

                            P2_SellOrders[P2_SellOrders.IndexOf(P2_SellOrders.First(o => o.ClientID == TradeMsg.NewClientOrderId))] = order;
                        }
                    }
                    else if (TradeMsg.OrderStatus == OrderStatus.PartiallyFilled)
                    {
                        // HANDLE PARTIAL FILLS (ANCHOR UNTIL FILLED)
                        var order = new OrderInfo(TradeMsg);
                        if (order.StratState == StrategyState.PARTIAL_FILL)
                        {
                            if (ID[1] == "Buy")
                                P2_BuyOrders[P2_BuyOrders.IndexOf(P2_BuyOrders.First(o => o.ClientID == TradeMsg.NewClientOrderId))] = order;
                            else if (ID[1] == "Sell")
                                P2_SellOrders[P2_SellOrders.IndexOf(P2_SellOrders.First(o => o.ClientID == TradeMsg.NewClientOrderId))] = order;
                        }
                    }

                    break;
                case ExecutionType.Cancelled:
                    break;
                case ExecutionType.Rejected:
                    LogOrderMessage(TradeMsg);
                    break;
                default:
                    LogOrderMessage(TradeMsg);
                    break;
            }
        }


        private void PopulateGridVals(decimal anchor)
        {
            if (!grid_values.Any())
            {
                grid_values.Add(anchor);
                var valUP = anchor;
                var valDOWN = anchor;
                for (int i = 2; i < 64; i++)
                {
                    valUP = Math.Round(valUP * (1 + SETTINGS.grid_interval), PriceRoundDigits);
                    valDOWN = Math.Round(valDOWN * (1 - SETTINGS.grid_interval), PriceRoundDigits);
                    grid_values.AddRange(new List<decimal>() { valUP, valDOWN });
                }
                grid_values = grid_values.Distinct().ToList();
            }
        }


        private void SetInitialPrices()
        {
            var ordList = new List<OrderInfo>();
            var changedList = new List<OrderInfo>();
            foreach (var tp in P2_BuyOrders.Where(o => o.StratState == StrategyState.TP_ENTERED))
            {
                if (tp.InitialPrice == 0M || !grid_values.Any(x => x == tp.InitialPrice))
                {
                    changedList.Add(new OrderInfo(tp));
                    var initPrice = tp.Price * (1 - SETTINGS.TakeProfitMargin);
                    tp.InitialPrice = grid_values.Aggregate((x, y) => Math.Abs(x - initPrice) < Math.Abs(y - initPrice) ? x : y);
                    ordList.Add(tp);
                }
            }
            P2_BuyOrders = P2_BuyOrders.Except(changedList).ToList();
            P2_BuyOrders.AddRange(ordList);

            ordList.Clear();
            changedList.Clear();
            foreach (var tp in P2_SellOrders.Where(o => o.StratState == StrategyState.TP_ENTERED))
            {
                if (tp.InitialPrice == 0M || !grid_values.Any(x => x == tp.InitialPrice))
                {
                    changedList.Add(new OrderInfo(tp));
                    var initPrice = tp.Price * (1 + SETTINGS.TakeProfitMargin);
                    tp.InitialPrice = grid_values.Aggregate((x, y) => Math.Abs(x - initPrice) < Math.Abs(y - initPrice) ? x : y);
                    ordList.Add(tp);
                }
            }
            P2_SellOrders = P2_SellOrders.Except(changedList).ToList();
            P2_SellOrders.AddRange(ordList);
        }


        public void ResetOrderLists()
        {
            P2_BuyOrders = new List<OrderInfo>();
            P2_SellOrders = new List<OrderInfo>();
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
