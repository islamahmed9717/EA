using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TelegramEAManager
{
    // Parsed signal data
    public class ParsedSignalData
    {
        public string Symbol { get; set; } = "";
        public string Direction { get; set; } = "";
        public string OrderType { get; set; } = "MARKET"; // MARKET, LIMIT, STOP
        public string OriginalSymbol { get; set; } = "";
        public string FinalSymbol { get; set; } = "";
        public double EntryPrice { get; set; }
        public double StopLoss { get; set; }
        public double TakeProfit1 { get; set; }
        public double TakeProfit2 { get; set; }
        public double TakeProfit3 { get; set; }

        // Helper to get full order description
        public string GetOrderTypeDescription()
        {
            if (OrderType == "MARKET")
                return Direction;
            else
                return $"{Direction} {OrderType}";
        }
    }
}
