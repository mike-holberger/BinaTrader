using BinanceExchange.API.Models.WebSocket;
using System.Collections.Concurrent;
using System.Threading;

namespace BinaTrader
{
    class BinaDataUser
    {        
        public ConcurrentQueue<UserDataUpdate> UserUpdateQueue { get; private set;  }

        public BinaDataUser()
        {
            UserUpdateQueue = new ConcurrentQueue<UserDataUpdate>();
        }     

        public void HandleTradeMessage(BinanceTradeOrderData data)
        {
            UserUpdateQueue.Enqueue(new UserDataUpdate(tradeOrds: data));
        }

        public void HandleOrderMessage(BinanceTradeOrderData data)
        {
            UserUpdateQueue.Enqueue(new UserDataUpdate(tradeOrds: data));
        }

        public void HandleAccountMessage(BinanceAccountUpdateData data)
        {
            // BALANCES NOT REQUIRED
            //UserUpdateQueue.Enqueue(new UserDataUpdate(account: data));            
        }
               
    }


    public class UserDataUpdate
    {
        public BinanceTradeOrderData TradeOrderData { get; set; }
        public BinanceAccountUpdateData AccountData { get; set; }

        public UserDataUpdate(BinanceTradeOrderData tradeOrds = null, BinanceAccountUpdateData account = null)
        {
            TradeOrderData = tradeOrds;
            AccountData = account;
        }
    }

}
