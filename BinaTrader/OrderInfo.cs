using BinanceExchange.API.Enums;
using BinanceExchange.API.Models.Request;
using BinanceExchange.API.Models.Response;
using BinanceExchange.API.Models.WebSocket;
using System;


namespace BinaTrader
{
    public class OrderInfo
    {
        public string Symbol { get; set; }
        public OrderSide Side { get; set; }
        public OrderStatus Status { get; set; }
        public decimal Price { get; set; }
        public decimal Qty { get; set; }
        public string ClientID { get; set; }
        public StrategyState StratState { get; set; }
        public decimal InitialPrice { get; set; }
        public decimal? BaseCurrReturn { get; set; }


        public OrderInfo()
        {

        }
        public OrderInfo(OrderInfo order)
        {
            Symbol = order.Symbol;
            Side = order.Side;
            Status = order.Status;
            Price = order.Price;
            Qty = order.Qty;
            ClientID = order.ClientID;
            StratState = order.StratState;
            InitialPrice = order.InitialPrice;
            BaseCurrReturn = BaseCurrReturn;
        }

        public OrderInfo(OrderResponse order)
        {
            Symbol = order.Symbol;
            Side = order.Side;
            Status = order.Status;
            Price = order.Price;
            Qty = order.OriginalQuantity;
            ClientID = order.ClientOrderId;
            BaseCurrReturn = null;

            //Always 'NEW' OrderStatus, from Initial loading
            if (order.Side.ToString() == ClientID.Split('_')[1])
                StratState = StrategyState.OPEN;
            else
                StratState = StrategyState.TP_ENTERED;
        }


        public OrderInfo(CreateOrderRequest order, StrategyState state)
        {
            Symbol = order.Symbol;
            Side = order.Side;
            Status = OrderStatus.New;
            if (order.Price != null)
                Price = Convert.ToDecimal(order.Price);
            Qty = order.Quantity;
            ClientID = order.NewClientOrderId;
            StratState = state;
            BaseCurrReturn = null;
        }


        public OrderInfo(BinanceTradeOrderData order)
        {
            Symbol = order.Symbol;
            Side = order.Side;
            Status = order.OrderStatus;
            Price = order.Price;
            Qty = order.Quantity;
            ClientID = order.NewClientOrderId;
            BaseCurrReturn = null;
            
            // SET StratStatus and BaseCurrReturn (if filled):
            var SideID = order.NewClientOrderId.Split('_')[1];

            if (order.OrderStatus == OrderStatus.New)
            {
                if (order.Side.ToString() == SideID)
                {
                    StratState = StrategyState.OPEN;
                    InitialPrice = Price;
                }
                else
                    StratState = StrategyState.TP_ENTERED;
            }
            else if (order.OrderStatus == OrderStatus.Filled)
            {
                if (order.Side.ToString() == SideID)
                {
                    StratState = StrategyState.OPEN_FILLED;
                    InitialPrice = Price;
                    //Set base curr for [SELL] take profit order
                    if (SideID == "Sell")
                        BaseCurrReturn = order.Price * order.Quantity;
                }
                else
                {
                    StratState = StrategyState.TP_FILLED;
                    //Set base curr for [BUY] new order
                    if (SideID == "Buy")
                        BaseCurrReturn = order.Price * order.Quantity;
                }

            }
            else if (order.OrderStatus == OrderStatus.PartiallyFilled)
            {
                // Set StratState to PARTIAL_FILL if it is not a TakeProfit order
                if (order.Side.ToString() == SideID)
                {
                    StratState = StrategyState.PARTIAL_FILL;
                    InitialPrice = Price;
                }
            }
        }
    }


    public enum StrategyState
    {
        OPEN_UNCONFIRMED = 5,
        OPEN = 1,
        PARTIAL_FILL = 4,
        OPEN_FILLED = 2,
        TP_UNCONFIRMED = 6,
        TP_ENTERED = 3,
        TP_FILLED = 0
    }
}
