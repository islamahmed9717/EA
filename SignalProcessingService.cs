using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Collections.Concurrent;
using System.Text;

namespace TelegramEAManager
{
    public class SignalProcessingService
    {
        private SymbolMapping symbolMapping = new SymbolMapping();
        private EASettings eaSettings = new EASettings();
        private List<ProcessedSignal> processedSignals = new List<ProcessedSignal>();
        private readonly string signalsHistoryFile = "signals_history.json";
        private readonly object fileLock = new object();

        // CRITICAL: Track processed messages to avoid duplicates
        private readonly ConcurrentDictionary<string, DateTime> processedMessageHashes = new ConcurrentDictionary<string, DateTime>();
        private readonly SemaphoreSlim fileWriteSemaphore = new SemaphoreSlim(1, 1);
        private DateTime lastCleanupTime = DateTime.Now;

        // Performance optimization: Background processing queue
        private readonly BlockingCollection<ProcessedSignal> signalQueue = new BlockingCollection<ProcessedSignal>(1000);
        private readonly CancellationTokenSource cancellationTokenSource = new CancellationTokenSource();
        private Task? backgroundProcessorTask;

        // Events
        public event EventHandler<ProcessedSignal>? SignalProcessed;
        public event EventHandler<string>? ErrorOccurred;
        public event EventHandler<string>? DebugMessage;

        public SignalProcessingService()
        {
            LoadSymbolMapping();
            LoadEASettings();
            LoadSignalsHistory();
            ClearSignalFileOnStartup();
            // Start background processor for better performance
            StartBackgroundProcessor();

            OnDebugMessage($"SignalProcessingService initialized - Loaded {processedSignals.Count} historical signals");
        }

        private void StartBackgroundProcessor()
        {
            backgroundProcessorTask = Task.Run(async () =>
            {
                var lastSaveTime = DateTime.Now;

                while (!cancellationTokenSource.Token.IsCancellationRequested)
                {
                    try
                    {
                        if (signalQueue.TryTake(out var signal, 100, cancellationTokenSource.Token))
                        {
                            await WriteSignalToEAFileAsync(signal);
                        }

                        // Periodic cleanup
                        if ((DateTime.Now - lastCleanupTime).TotalMinutes > 5)
                        {
                            CleanupOldMessageHashes();
                            lastCleanupTime = DateTime.Now;
                        }

                        // Periodic save of signals history (every 30 seconds)
                        if ((DateTime.Now - lastSaveTime).TotalSeconds > 30)
                        {
                            SaveSignalsHistory();
                            lastSaveTime = DateTime.Now;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        OnErrorOccurred($"Background processor error: {ex.Message}");
                    }
                }

                // Final save before exiting
                SaveSignalsHistory();
            });
        }

        private string GenerateMessageHash(string messageText, long channelId)
        {
            using (var md5 = System.Security.Cryptography.MD5.Create())
            {
                var inputBytes = System.Text.Encoding.UTF8.GetBytes($"{channelId}_{messageText}");
                var hashBytes = md5.ComputeHash(inputBytes);
                return Convert.ToBase64String(hashBytes);
            }
        }

        /// <summary>
        /// Clear signal file on startup to prevent old signals from being processed
        /// </summary>
        private void ClearSignalFileOnStartup()
        {
            try
            {
                if (!string.IsNullOrEmpty(eaSettings.MT4FilesPath))
                {
                    var filePath = Path.Combine(eaSettings.MT4FilesPath, "telegram_signals.txt");

                    if (File.Exists(filePath))
                    {
                        lock (fileLock)
                        {
                            using (var writer = new StreamWriter(filePath, false))
                            {
                                writer.WriteLine("# Telegram EA Signal File - CLEARED ON STARTUP");
                                writer.WriteLine($"# Startup Time: {DateTime.Now:yyyy.MM.dd HH:mm:ss} LOCAL");
                                writer.WriteLine("# Format: TIMESTAMP|CHANNEL_ID|CHANNEL_NAME|DIRECTION|SYMBOL|ENTRY|SL|TP1|TP2|TP3|STATUS|ORDER_TYPE");
                                writer.WriteLine("");
                            }
                        }

                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss} LOCAL] Signal file cleared on startup");
                    }
                }
            }
            catch (Exception ex)
            {
                OnErrorOccurred($"Failed to clear signal file on startup: {ex.Message}");
            }
        }

        public ProcessedSignal ProcessTelegramMessage(string messageText, long channelId, string channelName)
        {
            // Quick validation
            if (string.IsNullOrWhiteSpace(messageText))
            {
                return CreateEmptySignal(channelId, channelName, "Empty message");
            }

            // Generate message hash to check for duplicates
            var messageHash = GenerateMessageHash(messageText, channelId);

            // Check for recent duplicates
            if (processedMessageHashes.TryGetValue(messageHash, out DateTime processedTime))
            {
                if ((DateTime.Now - processedTime).TotalMinutes < 5)
                {
                    return CreateEmptySignal(channelId, channelName, "Duplicate - Already processed");
                }
            }

            // Mark as processed
            processedMessageHashes[messageHash] = DateTime.Now;

            var signal = new ProcessedSignal
            {
                Id = Guid.NewGuid().ToString(),
                DateTime = DateTime.Now,
                ChannelId = channelId,
                ChannelName = channelName,
                OriginalText = messageText,
                Status = "Processing..."
            };

            try
            {
                // Enhanced parsing with multiple format support
                var parsedData = ParseTradingSignalEnhanced(messageText);

                if (parsedData != null)
                {
                    signal.ParsedData = parsedData;
                    ApplySymbolMapping(signal.ParsedData);

                    if (ValidateSignal(signal.ParsedData))
                    {
                        // Queue for background processing (non-blocking)
                        if (!signalQueue.TryAdd(signal, 100))
                        {
                            // If queue is full, process synchronously
                            Task.Run(async () => await WriteSignalToEAFileAsync(signal));
                        }

                        signal.Status = "Processed - Sent to EA";

                        lock (processedSignals)
                        {
                            processedSignals.Add(signal);
                            if (processedSignals.Count > 1000)
                            {
                                processedSignals.RemoveRange(0, processedSignals.Count - 1000);
                            }
                        }

                        // Async save without blocking
                        Task.Run(async () => await SaveSignalsHistoryAsync());
                        OnSignalProcessed(signal);
                    }
                    else
                    {
                        signal.Status = "Invalid - Missing required data";
                    }
                }
                else
                {
                    signal.Status = "No trading signal detected";
                }
            }
            catch (Exception ex)
            {
                signal.Status = $"Error - {ex.Message}";
                signal.ErrorMessage = ex.ToString();
                OnErrorOccurred($"Error processing signal: {ex.Message}");
            }

            return signal;
        }

        private ProcessedSignal CreateEmptySignal(long channelId, string channelName, string status)
        {
            return new ProcessedSignal
            {
                Id = Guid.NewGuid().ToString(),
                DateTime = DateTime.Now,
                ChannelId = channelId,
                ChannelName = channelName,
                OriginalText = "",
                Status = status,
                ParsedData = new ParsedSignalData()
            };
        }

        // NEW: Add this method to specifically handle XAUUSD signals
        // NEW: Add this method to specifically handle XAUUSD signals
        private bool TryExtractXAUUSDSignal(string text, out ParsedSignalData? signal)
        {
            signal = null;

            // Check if text contains XAUUSD or GOLD
            if (!text.Contains("XAUUSD") && !text.Contains("GOLD") && !text.Contains("XAU"))
                return false;

            // Look for BUY/SELL
            var directionMatch = Regex.Match(text, @"\b(BUY|SELL)\b");
            if (!directionMatch.Success)
                return false;

            signal = new ParsedSignalData
            {
                Direction = directionMatch.Value,
                Symbol = "XAUUSD",
                OriginalSymbol = "XAUUSD",
                FinalSymbol = "XAUUSD" // Ensure this is set
            };

            // Extract price after NOW or after direction
            var pricePatterns = new[]
            {
                @"NOW\s+(\d{4,5}(?:\.\d+)?)",          // NOW 3342
                @"SELL\s+NOW\s+(\d{4,5}(?:\.\d+)?)",   // SELL NOW 3342
                @"BUY\s+NOW\s+(\d{4,5}(?:\.\d+)?)",    // BUY NOW 3342
                @"(?:BUY|SELL)\s+(\d{4,5}(?:\.\d+)?)"  // SELL 3342
            };

            foreach (var pattern in pricePatterns)
            {
                var priceMatch = Regex.Match(text, pattern);
                if (priceMatch.Success && double.TryParse(priceMatch.Groups[1].Value, out double price))
                {
                    if (price > 1000 && price < 5000) // Valid XAUUSD range
                    {
                        signal.EntryPrice = price;
                        break;
                    }
                }
            }

            ExtractStopLossAndTakeProfit(text, signal);
            return true;
        }

        /// <summary>
        /// Enhanced signal parsing with support for multiple formats
        /// </summary>
        private ParsedSignalData? ParseTradingSignalEnhanced(string messageText)
        {
            if (string.IsNullOrWhiteSpace(messageText))
                return null;

            var text = NormalizeText(messageText);
            OnDebugMessage($"Normalized text: {text.Substring(0, Math.Min(100, text.Length))}...");

            // First, try XAUUSD specific parsing
            if (TryExtractXAUUSDSignal(text, out var xauSignal))
            {
                OnDebugMessage("XAUUSD signal detected using specific parser");
                return xauSignal;
            }

            // Try multiple parsing strategies
            var parsedData =
                ParseFormat1(text) ??          // Standard format: BUY/SELL SYMBOL
                ParseFormat2(text) ??          // Alternative: SYMBOL BUY/SELL
                ParseFormat3(text) ??          // Emoji-based signals
                ParseFormat4(text) ??          // Structured format with labels
                ParseFormat5(text) ??          // Compact format
                ParseFormatPending(text) ??    // NEW: Pending orders format
                ParseFormatCustom(text);       // Custom patterns

            if (parsedData != null)
            {
                // Extract additional data that might have been missed
                ExtractPrices(text, parsedData);
                ExtractOrderType(text, parsedData); // NEW: Extract order type
                NormalizeData(parsedData);
            }

            return parsedData;
        }

        // NEW: Extract order type from text
        private void ExtractOrderType(string text, ParsedSignalData signal)
        {
            // If order type not already set, check for keywords
            if (signal.OrderType == "MARKET" || string.IsNullOrEmpty(signal.OrderType))
            {
                if (Regex.IsMatch(text, @"\b(BUY|SELL)\s+LIMIT\b", RegexOptions.IgnoreCase))
                {
                    signal.OrderType = "LIMIT";
                }
                else if (Regex.IsMatch(text, @"\b(BUY|SELL)\s+STOP\b", RegexOptions.IgnoreCase))
                {
                    signal.OrderType = "STOP";
                }
                else if (Regex.IsMatch(text, @"\bLIMIT\s+ORDER\b", RegexOptions.IgnoreCase))
                {
                    signal.OrderType = "LIMIT";
                }
                else if (Regex.IsMatch(text, @"\bSTOP\s+ORDER\b", RegexOptions.IgnoreCase))
                {
                    signal.OrderType = "STOP";
                }
                else if (Regex.IsMatch(text, @"\bPENDING\b", RegexOptions.IgnoreCase))
                {
                    // Try to determine if it's limit or stop based on price context
                    if (signal.Direction == "BUY" && signal.EntryPrice > 0)
                    {
                        // For buy orders, if entry is mentioned with "below" it's likely a limit
                        if (Regex.IsMatch(text, @"\bBELOW\b", RegexOptions.IgnoreCase))
                            signal.OrderType = "LIMIT";
                        else if (Regex.IsMatch(text, @"\bABOVE\b", RegexOptions.IgnoreCase))
                            signal.OrderType = "STOP";
                    }
                    else if (signal.Direction == "SELL" && signal.EntryPrice > 0)
                    {
                        // For sell orders, if entry is mentioned with "above" it's likely a limit
                        if (Regex.IsMatch(text, @"\bABOVE\b", RegexOptions.IgnoreCase))
                            signal.OrderType = "LIMIT";
                        else if (Regex.IsMatch(text, @"\bBELOW\b", RegexOptions.IgnoreCase))
                            signal.OrderType = "STOP";
                    }
                }
                // Check for instant/market execution keywords
                else if (Regex.IsMatch(text, @"\b(NOW|INSTANT|MARKET|CURRENT|IMMEDIATELY)\b", RegexOptions.IgnoreCase))
                {
                    signal.OrderType = "MARKET";
                }
            }
        }

        private ParsedSignalData? ParseFormatPending(string text)
        {
            // Pattern for pending orders: BUY LIMIT, SELL STOP, etc.
            var match = Regex.Match(text, @"\b(BUY|SELL)\s+(LIMIT|STOP)\s+([A-Z]{2,}(?:[A-Z]{0,3}|\/[A-Z]{3})?)\b");
            if (!match.Success)
            {
                // Try reverse pattern: EURUSD BUY LIMIT
                match = Regex.Match(text, @"\b([A-Z]{2,}(?:[A-Z]{0,3}|\/[A-Z]{3})?)\s+(BUY|SELL)\s+(LIMIT|STOP)\b");
                if (match.Success)
                {
                    var signal = new ParsedSignalData
                    {
                        Symbol = match.Groups[1].Value,
                        OriginalSymbol = match.Groups[1].Value,
                        Direction = match.Groups[2].Value,
                        OrderType = match.Groups[3].Value
                    };
                    ExtractStopLossAndTakeProfit(text, signal);
                    return signal;
                }
                return null;
            }

            var signalData = new ParsedSignalData
            {
                Direction = match.Groups[1].Value,
                OrderType = match.Groups[2].Value,
                Symbol = match.Groups[3].Value,
                OriginalSymbol = match.Groups[3].Value
            };

            ExtractStopLossAndTakeProfit(text, signalData);
            return signalData;
        }

        private string NormalizeText(string text)
        {
            // Normalize text for better parsing
            text = text.ToUpper();
            text = Regex.Replace(text, @"\r\n|\r|\n", " ");
            text = Regex.Replace(text, @"\s+", " ");
            text = Regex.Replace(text, @"[^\w\s\.\,\:\@\-\/\+\#\$\%\&\*\(\)\[\]\{\}]", " ");
            return text.Trim();
        }

        // Format 1: Standard "BUY/SELL SYMBOL" format - FIXED VERSION
        private ParsedSignalData? ParseFormat1(string text)
        {
            // Match patterns like "BUY XAUUSD", "SELL NOW XAUUSD", "BUY NOW 3342"
            var match = Regex.Match(text, @"\b(BUY|SELL|LONG|SHORT)\s+(?:NOW\s+)?([A-Z]{2,}(?:[A-Z]{0,3}|\/[A-Z]{3})?)\b");
            if (!match.Success) return null;

            var direction = NormalizeDirection(match.Groups[1].Value);
            var potentialSymbol = match.Groups[2].Value;

            // Skip if the captured "symbol" is actually "NOW" or a number
            if (potentialSymbol == "NOW" || Regex.IsMatch(potentialSymbol, @"^\d+$"))
            {
                // Try to find the real symbol elsewhere in the text
                var symbolMatch = Regex.Match(text, @"\b(XAUUSD|EURUSD|GBPUSD|USDJPY|USDCHF|AUDUSD|USDCAD|NZDUSD|[A-Z]{6,7})\b");
                if (symbolMatch.Success)
                {
                    potentialSymbol = symbolMatch.Value;
                }
                else
                {
                    return null;
                }
            }

            var signal = new ParsedSignalData
            {
                Direction = direction,
                Symbol = potentialSymbol,
                OriginalSymbol = potentialSymbol
            };

            ExtractStopLossAndTakeProfit(text, signal);

            // Extract entry price from patterns like "SELL NOW 3342"
            var priceAfterNow = Regex.Match(text, @"\bNOW\s+(\d+(?:\.\d+)?)\b");
            if (priceAfterNow.Success && double.TryParse(priceAfterNow.Groups[1].Value, out double price))
            {
                signal.EntryPrice = price;
            }

            return signal;
        }

        // Format 2: "SYMBOL BUY/SELL" format
        private ParsedSignalData? ParseFormat2(string text)
        {
            var match = Regex.Match(text, @"\b([A-Z]{2,}(?:[A-Z]{0,3}|\/[A-Z]{3})?)\s+(BUY|SELL|LONG|SHORT)\b");
            if (!match.Success) return null;

            var signal = new ParsedSignalData
            {
                Symbol = match.Groups[1].Value,
                OriginalSymbol = match.Groups[1].Value,
                Direction = NormalizeDirection(match.Groups[2].Value)
            };

            ExtractStopLossAndTakeProfit(text, signal);
            return signal;
        }

        // Format 3: Emoji-based signals
        private ParsedSignalData? ParseFormat3(string text)
        {
            // Look for buy/sell indicators with emojis
            var buyPattern = @"(?:🟢|✅|📈|⬆️|🚀|💹)\s*([A-Z]{2,}(?:[A-Z]{0,3}|\/[A-Z]{3})?)";
            var sellPattern = @"(?:🔴|❌|📉|⬇️|🔻|💔)\s*([A-Z]{2,}(?:[A-Z]{0,3}|\/[A-Z]{3})?)";

            var buyMatch = Regex.Match(text, buyPattern);
            var sellMatch = Regex.Match(text, sellPattern);

            if (buyMatch.Success)
            {
                var signal = new ParsedSignalData
                {
                    Direction = "BUY",
                    Symbol = buyMatch.Groups[1].Value,
                    OriginalSymbol = buyMatch.Groups[1].Value
                };
                ExtractStopLossAndTakeProfit(text, signal);
                return signal;
            }

            if (sellMatch.Success)
            {
                var signal = new ParsedSignalData
                {
                    Direction = "SELL",
                    Symbol = sellMatch.Groups[1].Value,
                    OriginalSymbol = sellMatch.Groups[1].Value
                };
                ExtractStopLossAndTakeProfit(text, signal);
                return signal;
            }

            return null;
        }

        // Format 4: Structured format with clear labels
        private ParsedSignalData? ParseFormat4(string text)
        {
            var symbolMatch = Regex.Match(text, @"(?:PAIR|SYMBOL|CURRENCY|ASSET)[:\s]*([A-Z]{2,}(?:[A-Z]{0,3}|\/[A-Z]{3})?)");
            var directionMatch = Regex.Match(text, @"(?:ACTION|DIRECTION|SIGNAL|TYPE)[:\s]*(BUY|SELL|LONG|SHORT)");

            if (symbolMatch.Success && directionMatch.Success)
            {
                var signal = new ParsedSignalData
                {
                    Symbol = symbolMatch.Groups[1].Value,
                    OriginalSymbol = symbolMatch.Groups[1].Value,
                    Direction = NormalizeDirection(directionMatch.Groups[1].Value)
                };
                ExtractStopLossAndTakeProfit(text, signal);
                return signal;
            }

            return null;
        }

        // Format 5: Compact format (e.g., "EURUSD-BUY@1.0890")
        private ParsedSignalData? ParseFormat5(string text)
        {
            var match = Regex.Match(text, @"\b([A-Z]{2,}(?:[A-Z]{0,3}|\/[A-Z]{3})?)\s*[-]\s*(BUY|SELL|LONG|SHORT)\s*[@]?\s*(\d+\.?\d*)");
            if (!match.Success) return null;

            var signal = new ParsedSignalData
            {
                Symbol = match.Groups[1].Value,
                OriginalSymbol = match.Groups[1].Value,
                Direction = NormalizeDirection(match.Groups[2].Value),
                EntryPrice = double.TryParse(match.Groups[3].Value, out var entry) ? entry : 0
            };

            ExtractStopLossAndTakeProfit(text, signal);
            return signal;
        }

        // Custom format parser for special cases
        private ParsedSignalData? ParseFormatCustom(string text)
        {
            // Try to find any combination of known symbols and directions
            var symbolPatterns = GetAllSymbolPatterns();
            var directionPatterns = new[] { "BUY", "SELL", "LONG", "SHORT", "BULLISH", "BEARISH", "UP", "DOWN" };

            foreach (var symbolPattern in symbolPatterns)
            {
                var symbolMatch = Regex.Match(text, $@"\b{symbolPattern}\b");
                if (symbolMatch.Success)
                {
                    foreach (var direction in directionPatterns)
                    {
                        if (text.Contains(direction))
                        {
                            var signal = new ParsedSignalData
                            {
                                Symbol = symbolMatch.Value,
                                OriginalSymbol = symbolMatch.Value,
                                Direction = NormalizeDirection(direction)
                            };
                            ExtractStopLossAndTakeProfit(text, signal);
                            return signal;
                        }
                    }
                }
            }

            return null;
        }

        // 4. COMPREHENSIVE SYMBOL LIST - Update GetAllSymbolPatterns
        private string[] GetAllSymbolPatterns()
        {
            return new[]
            {
        // Major Forex Pairs
        "EURUSD", "GBPUSD", "USDJPY", "USDCHF", "AUDUSD", "USDCAD", "NZDUSD",
        
        // Minor Forex Pairs
        "EURJPY", "GBPJPY", "EURGBP", "EURAUD", "EURCAD", "EURNZD", "EURCHF",
        "GBPAUD", "GBPCAD", "GBPNZD", "GBPCHF", "AUDJPY", "CADJPY", "NZDJPY",
        "AUDNZD", "AUDCAD", "AUDCHF", "NZDCAD", "NZDCHF", "CADCHF", "CHFJPY",
        
        // Exotic Pairs
        "USDZAR", "USDTRY", "USDMXN", "USDSEK", "USDNOK", "USDDKK", "USDPLN",
        "USDHUF", "USDCZK", "USDSGD", "USDHKD", "USDCNH", "USDRUB", "USDINR",
        "EURTRY", "EURPLN", "EURHUF", "EURCZK", "EURSEK", "EURNOK", "EURDKK",
        "GBPTRY", "GBPPLN", "GBPSEK", "GBPNOK", "GBPDKK",
        
        // Metals
        "XAUUSD", "GOLD", "XAGUSD", "SILVER", "XPTUSD", "PLATINUM", "XPDUSD", "PALLADIUM",
        "XAUEUR", "XAGEUR", "XAUAUD", "XAUGBP", "XAUCHF", "XAUJPY",
        
        // Energy
        "USOIL", "UKOIL", "BRENT", "WTI", "CRUDE", "NATGAS", "NGAS", "GAS",
        
        // Indices
        "US30", "DJIA", "DOW", "DJ30", "US100", "NAS100", "NASDAQ", "NDX", "USTEC",
        "SPX500", "SP500", "SPX", "US500", "USA500",
        "GER30", "GER40", "DAX", "DAX30", "DAX40", "DE30", "DE40",
        "UK100", "FTSE", "FTSE100", "UKX",
        "FRA40", "CAC", "CAC40", "FR40",
        "EU50", "STOXX50", "EUSTX50",
        "JPN225", "NIKKEI", "N225", "JP225",
        "AUS200", "ASX200", "AU200",
        "HK50", "HSI", "HANG", "HANGSENG",
        "CHINA50", "CHN50", "CN50",
        "ESP35", "IBEX", "IBEX35",
        "ITA40", "IT40", "MIB40",
        "SUI20", "SMI", "SMI20",
        "NED25", "AEX", "AEX25",
        
        // Crypto (Major)
        "BTCUSD", "BITCOIN", "BTC", "ETHUSD", "ETHEREUM", "ETH",
        "XRPUSD", "RIPPLE", "XRP", "LTCUSD", "LITECOIN", "LTC",
        "BCHUSD", "BITCOINCASH", "BCH", "BNBUSD", "BINANCE", "BNB",
        "ADAUSD", "CARDANO", "ADA", "DOTUSD", "POLKADOT", "DOT",
        "LINKUSD", "CHAINLINK", "LINK", "XLMUSD", "STELLAR", "XLM",
        "DOGEUSD", "DOGECOIN", "DOGE", "UNIUSD", "UNISWAP", "UNI",
        "SOLUSD", "SOLANA", "SOL", "MATICUSD", "POLYGON", "MATIC",
        "AVAXUSD", "AVALANCHE", "AVAX", "ATOMUSD", "COSMOS", "ATOM",
        
        // Crypto pairs with other bases
        "BTCEUR", "ETHEUR", "BTCGBP", "ETHGBP", "BTCJPY", "ETHJPY",
        "BTCAUD", "ETHAUD", "BTCCAD", "ETHCAD",
        
        // Commodities
        "CORN", "WHEAT", "SOYBEAN", "SOYB", "SUGAR", "COFFEE", "COCOA",
        "COTTON", "RICE", "OATS", "CATTLE", "HOGS", "COPPER", "ZINC",
        "ALUMINUM", "NICKEL", "LEAD", "TIN",
        
        // Stocks (Common CFDs)
        "AAPL", "APPLE", "GOOGL", "GOOGLE", "MSFT", "MICROSOFT",
        "AMZN", "AMAZON", "FB", "META", "FACEBOOK", "TSLA", "TESLA",
        "NVDA", "NVIDIA", "JPM", "JPMORGAN", "BAC", "BANKOFAMERICA",
        "V", "VISA", "MA", "MASTERCARD", "JNJ", "JOHNSON",
        "WMT", "WALMART", "PG", "PROCTER", "UNH", "UNITEDHEALTH",
        "HD", "HOMEDEPOT", "DIS", "DISNEY", "PYPL", "PAYPAL",
        "NFLX", "NETFLIX", "ADBE", "ADOBE", "CRM", "SALESFORCE",
        "PFE", "PFIZER", "NVDA", "NVIDIA", "AMD", "INTEL", "INTC",
        
        // Bonds/Treasuries
        "USB02Y", "USB05Y", "USB10Y", "USB30Y", "BUND", "GILT", "JGB",
        
        // Short forms / Nicknames
        "EU", "GU", "UJ", "UC", "AU", "NU", "UCAD", "GJ", "EJ", "EG",
        "GA", "GN", "EA", "EN", "AJ", "NJ", "GOLD", "OIL", "NATGAS"
    };
        }

        private void ExtractStopLossAndTakeProfit(string text, ParsedSignalData signal)
        {
            // Enhanced SL extraction
            var slPatterns = new[]
            {
                @"(?:SL|STOP\s*LOSS|STOPLOSS|S\.L|S\/L|STOP)\s*[:=@]?\s*(\d+\.?\d*)",
                @"(?:STOP|SL)\s*(?:AT|@)\s*(\d+\.?\d*)",
                @"(?:RISK|INVALIDATION)\s*[:=@]?\s*(\d+\.?\d*)"
            };

            foreach (var pattern in slPatterns)
            {
                var match = Regex.Match(text, pattern, RegexOptions.IgnoreCase);
                if (match.Success && double.TryParse(match.Groups[1].Value, out double sl))
                {
                    signal.StopLoss = sl;
                    break;
                }
            }

            // Enhanced TP extraction - Multiple TPs
            var tpPatterns = new[]
            {
                (@"(?:TP|TAKE\s*PROFIT|TAKEPROFIT|T\.P|T\/P|TARGET)\s*1?\s*[:=@]?\s*(\d+\.?\d*)", 1),
                (@"(?:TP|TARGET)\s*2\s*[:=@]?\s*(\d+\.?\d*)", 2),
                (@"(?:TP|TARGET)\s*3\s*[:=@]?\s*(\d+\.?\d*)", 3),
                (@"(?:PROFIT|GOAL|OBJECTIVE)\s*[:=@]?\s*(\d+\.?\d*)", 1),
                (@"(?:1ST|FIRST)\s*(?:TP|TARGET)\s*[:=@]?\s*(\d+\.?\d*)", 1),
                (@"(?:2ND|SECOND)\s*(?:TP|TARGET)\s*[:=@]?\s*(\d+\.?\d*)", 2),
                (@"(?:3RD|THIRD)\s*(?:TP|TARGET)\s*[:=@]?\s*(\d+\.?\d*)", 3),
            };

            foreach (var (pattern, level) in tpPatterns)
            {
                var match = Regex.Match(text, pattern, RegexOptions.IgnoreCase);
                if (match.Success && double.TryParse(match.Groups[1].Value, out double tp))
                {
                    switch (level)
                    {
                        case 1: signal.TakeProfit1 = tp; break;
                        case 2: signal.TakeProfit2 = tp; break;
                        case 3: signal.TakeProfit3 = tp; break;
                    }
                }
            }

            // Try to extract TPs from lists (e.g., "TP: 1.0900, 1.0950, 1.1000")
            var listMatch = Regex.Match(text, @"(?:TP|TARGET|PROFIT)S?\s*[:=]?\s*((?:\d+\.?\d*\s*[,;]\s*)+\d+\.?\d*)");
            if (listMatch.Success)
            {
                var tps = listMatch.Groups[1].Value.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => double.TryParse(s.Trim(), out var v) ? v : 0)
                    .Where(v => v > 0)
                    .ToList();

                if (tps.Count > 0 && signal.TakeProfit1 == 0) signal.TakeProfit1 = tps[0];
                if (tps.Count > 1 && signal.TakeProfit2 == 0) signal.TakeProfit2 = tps[1];
                if (tps.Count > 2 && signal.TakeProfit3 == 0) signal.TakeProfit3 = tps[2];
            }
        }

        private void ExtractPrices(string text, ParsedSignalData signal)
        {
            // Extract entry price if not already found
            if (signal.EntryPrice == 0)
            {
                var entryPatterns = new[]
                {
                    @"(?:ENTRY|ENTER|PRICE|BUY\s*AT|SELL\s*AT|@|EXECUTION)\s*[:=@]?\s*(\d+\.?\d*)",
                    @"(?:MARKET|NOW|CURRENT)\s*(?:PRICE)?\s*[:=@]?\s*(\d+\.?\d*)",
                    @"(?:OPEN|LIMIT)\s*[:=@]?\s*(\d+\.?\d*)"
                };

                foreach (var pattern in entryPatterns)
                {
                    var match = Regex.Match(text, pattern, RegexOptions.IgnoreCase);
                    if (match.Success && double.TryParse(match.Groups[1].Value, out double entry))
                    {
                        signal.EntryPrice = entry;
                        break;
                    }
                }
            }

            // Look for price ranges (e.g., "1.0890-1.0900")
            var rangeMatch = Regex.Match(text, @"(\d+\.?\d*)\s*[-]\s*(\d+\.?\d*)");
            if (rangeMatch.Success)
            {
                if (double.TryParse(rangeMatch.Groups[1].Value, out var price1) &&
                    double.TryParse(rangeMatch.Groups[2].Value, out var price2))
                {
                    // Determine which is entry and which is TP/SL based on direction
                    if (signal.Direction == "BUY")
                    {
                        signal.EntryPrice = signal.EntryPrice == 0 ? Math.Min(price1, price2) : signal.EntryPrice;
                        signal.TakeProfit1 = signal.TakeProfit1 == 0 ? Math.Max(price1, price2) : signal.TakeProfit1;
                    }
                    else if (signal.Direction == "SELL")
                    {
                        signal.EntryPrice = signal.EntryPrice == 0 ? Math.Max(price1, price2) : signal.EntryPrice;
                        signal.TakeProfit1 = signal.TakeProfit1 == 0 ? Math.Min(price1, price2) : signal.TakeProfit1;
                    }
                }
            }
        }

        private void NormalizeData(ParsedSignalData signal)
        {
            // Normalize symbol
            signal.Symbol = NormalizeSymbol(signal.Symbol);

            // Ensure OriginalSymbol is set
            if (string.IsNullOrEmpty(signal.OriginalSymbol))
                signal.OriginalSymbol = signal.Symbol;

            // Validate and fix price logic if needed
            if (signal.StopLoss > 0 && signal.TakeProfit1 > 0)
            {
                // Auto-correct obvious mistakes
                if (signal.Direction == "BUY" && signal.StopLoss > signal.TakeProfit1)
                {
                    // Swap them
                    (signal.StopLoss, signal.TakeProfit1) = (signal.TakeProfit1, signal.StopLoss);
                }
                else if (signal.Direction == "SELL" && signal.StopLoss < signal.TakeProfit1)
                {
                    // Swap them
                    (signal.StopLoss, signal.TakeProfit1) = (signal.TakeProfit1, signal.StopLoss);
                }
            }
        }

        private string NormalizeDirection(string direction)
        {
            direction = direction.ToUpper().Trim();

            var buyAliases = new[] { "BUY", "LONG", "BULLISH", "UP", "CALL" };
            var sellAliases = new[] { "SELL", "SHORT", "BEARISH", "DOWN", "PUT" };

            if (buyAliases.Contains(direction))
                return "BUY";
            if (sellAliases.Contains(direction))
                return "SELL";

            return direction; // Return as-is if not recognized
        }

        private string NormalizeSymbol(string symbol)
        {
            if (string.IsNullOrEmpty(symbol))
                return "";

            var normalized = symbol.Replace("/", "").Replace("-", "").Replace("_", "").ToUpper().Trim();

            // Expand common abbreviations
            var symbolMappings = new Dictionary<string, string>
            {
                { "EU", "EURUSD" },
                { "GU", "GBPUSD" },
                { "UJ", "USDJPY" },
                { "UC", "USDCHF" },
                { "AU", "AUDUSD" },
                { "NU", "NZDUSD" },
                { "UCAD", "USDCAD" },
                { "GJ", "GBPJPY" },
                { "EJ", "EURJPY" },
                { "EG", "EURGBP" },
                { "XAU", "XAUUSD" },
                { "XAG", "XAGUSD" },
                { "BTC", "BTCUSD" },
                { "ETH", "ETHUSD" },
                { "OIL", "USOIL" },
                { "GER", "GER30" },
                { "NAS", "NAS100" },
                { "SPX", "SPX500" },
                { "DJI", "US30" },
                { "DOW", "US30" }
            };

            if (symbolMappings.ContainsKey(normalized))
            {
                normalized = symbolMappings[normalized];
            }

            return normalized;
        }
        private async Task WriteSignalToEAFileAsync(ProcessedSignal signal)
        {
            if (string.IsNullOrEmpty(eaSettings.MT4FilesPath))
            {
                throw new InvalidOperationException("MT4 files path not configured");
            }

            var filePath = Path.Combine(eaSettings.MT4FilesPath, "telegram_signals.txt");
            var directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var signalText = FormatSignalForEA(signal);
            var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            try
            {
                await fileWriteSemaphore.WaitAsync(timeoutCts.Token);

                try
                {
                    var isDuplicate = await CheckForDuplicateSignalAsync(filePath, signal);

                    if (!isDuplicate)
                    {
                        using (var stream = new FileStream(filePath, FileMode.Append, FileAccess.Write, FileShare.Read))
                        using (var writer = new StreamWriter(stream, Encoding.UTF8))
                        {
                            await writer.WriteLineAsync(signalText);
                            await writer.FlushAsync();
                        }

                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Signal written to file successfully");
                    }
                    else
                    {
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Skipped duplicate signal");
                    }
                }
                finally
                {
                    fileWriteSemaphore.Release();
                }
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] File write timeout - signal may not have been written");
                throw new TimeoutException("File write operation timed out");
            }
        }


        private async Task<bool> CheckForDuplicateSignalAsync(string filePath, ProcessedSignal signal)
        {
            if (!File.Exists(filePath))
                return false;

            try
            {
                var lines = await File.ReadAllLinesAsync(filePath);
                var recentLines = lines.TakeLast(50);

                var signalSignature = $"|{signal.ChannelId}|{signal.ChannelName}|{signal.ParsedData?.Direction ?? ""}|{signal.ParsedData?.Symbol ?? ""}|";
                var cutoffTime = DateTime.Now.AddMinutes(-10);

                foreach (var line in recentLines)
                {
                    if (line.Contains(signalSignature) && !line.StartsWith("#"))
                    {
                        var parts = line.Split('|');
                        if (parts.Length > 0 && DateTime.TryParse(parts[0], out var lineTime))
                        {
                            if (lineTime > cutoffTime)
                            {
                                return true;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error checking duplicates: {ex.Message}");
            }

            return false;
        }
        private void CleanupOldMessageHashes()
        {
            var cutoffTime = DateTime.Now.AddMinutes(-10);
            var keysToRemove = processedMessageHashes
                .Where(kvp => kvp.Value < cutoffTime)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var key in keysToRemove)
            {
                processedMessageHashes.TryRemove(key, out _);
            }
        }
        private async Task SaveSignalsHistoryAsync()
        {
            try
            {
                List<ProcessedSignal> signalsToSave;
                lock (processedSignals)
                {
                    signalsToSave = processedSignals.ToList();
                }

                var json = JsonConvert.SerializeObject(signalsToSave, Formatting.Indented);
                await File.WriteAllTextAsync(signalsHistoryFile, json);
            }
            catch (Exception ex)
            {
                OnErrorOccurred($"Failed to save signals history: {ex.Message}");
            }
        }
        public async Task CleanupProcessedSignalsAsync()
        {
            try
            {
                if (string.IsNullOrEmpty(eaSettings.MT4FilesPath))
                    return;

                var filePath = Path.Combine(eaSettings.MT4FilesPath, "telegram_signals.txt");

                if (!File.Exists(filePath))
                    return;

                await fileWriteSemaphore.WaitAsync();
                try
                {
                    var lines = await File.ReadAllLinesAsync(filePath);
                    var newLines = new List<string>();
                    var now = DateTime.UtcNow;

                    foreach (var line in lines)
                    {
                        if (line.StartsWith("#") || string.IsNullOrWhiteSpace(line))
                        {
                            newLines.Add(line);
                            continue;
                        }

                        var parts = line.Split('|');
                        if (parts.Length >= 11)
                        {
                            if (DateTime.TryParse(parts[0], out DateTime signalTime))
                            {
                                var ageMinutes = (now - signalTime).TotalMinutes;
                                if (ageMinutes <= 10 || parts[10] == "PROCESSED")
                                {
                                    if (!(parts[10] == "PROCESSED" && ageMinutes > 30))
                                    {
                                        newLines.Add(line);
                                    }
                                }
                            }
                        }
                    }

                    await File.WriteAllLinesAsync(filePath, newLines);
                    OnDebugMessage($"Cleanup completed - kept {newLines.Count(l => !l.StartsWith("#") && !string.IsNullOrWhiteSpace(l))} signals");
                }
                finally
                {
                    fileWriteSemaphore.Release();
                }
            }
            catch (Exception ex)
            {
                OnErrorOccurred($"Failed to cleanup signals: {ex.Message}");
                OnDebugMessage($"Cleanup error: {ex}");
            }
        }
        // 6. UPDATE FormatSignalForEA to include order type
        private string FormatSignalForEA(ProcessedSignal signal)
        {
            var localTime = DateTime.Now;
            var timestampFormatted = localTime.ToString("yyyy.MM.dd HH:mm:ss");

            // Format: TIMESTAMP|CHANNEL_ID|CHANNEL_NAME|DIRECTION|SYMBOL|ENTRY|SL|TP1|TP2|TP3|STATUS|ORDER_TYPE
            var formatted = $"{timestampFormatted}|" +
                            $"{signal.ChannelId}|" +
                            $"{signal.ChannelName}|" +
                            $"{signal.ParsedData?.Direction ?? "BUY"}|" +
                            $"{signal.ParsedData?.FinalSymbol ?? signal.ParsedData?.Symbol ?? "EURUSD"}|" +
                            $"{(signal.ParsedData?.EntryPrice ?? 0):F5}|" +
                            $"{(signal.ParsedData?.StopLoss ?? 0):F5}|" +
                            $"{(signal.ParsedData?.TakeProfit1 ?? 0):F5}|" +
                            $"{(signal.ParsedData?.TakeProfit2 ?? 0):F5}|" +
                            $"{(signal.ParsedData?.TakeProfit3 ?? 0):F5}|" +
                            $"NEW|" +
                            $"{signal.ParsedData?.OrderType ?? "MARKET"}";

            Console.WriteLine($"[{localTime:HH:mm:ss} LOCAL] Writing signal: {signal.ParsedData?.Symbol} {signal.ParsedData?.GetOrderTypeDescription()}");

            return formatted;
        }
        /// <summary>
        /// Parse trading signal from message text using regex patterns
        /// </summary>
        private ParsedSignalData? ParseTradingSignal(string messageText)
        {
            if (string.IsNullOrWhiteSpace(messageText))
            {
                OnDebugMessage("Message text is empty or whitespace");
                return null;
            }

            var text = messageText.ToUpper().Replace("\n", " ").Replace("\r", " ");
            OnDebugMessage($"Normalized text for parsing: {text.Substring(0, Math.Min(100, text.Length))}...");

            var signalData = new ParsedSignalData();

            // Extract direction (BUY/SELL) - improved pattern
            var directionMatch = Regex.Match(text, @"\b(BUY|SELL|LONG|SHORT)\b");
            if (!directionMatch.Success)
            {
                OnDebugMessage("No trading direction found in message");
                return null;
            }

            var direction = directionMatch.Value;
            if (direction == "LONG") direction = "BUY";
            if (direction == "SHORT") direction = "SELL";

            signalData.Direction = direction;
            OnDebugMessage($"Direction detected: {direction}");

            // Extract symbol - comprehensive patterns
            var symbolPatterns = new[]
            {
                // Major Forex Pairs
                @"\b(EUR\/USD|EURUSD|EUR-USD|EU)\b",
                @"\b(GBP\/USD|GBPUSD|GBP-USD|GU)\b",
                @"\b(USD\/JPY|USDJPY|USD-JPY|UJ)\b",
                @"\b(USD\/CHF|USDCHF|USD-CHF|UC)\b",
                @"\b(AUD\/USD|AUDUSD|AUD-USD|AU)\b",
                @"\b(USD\/CAD|USDCAD|USD-CAD|UCAD)\b",
                @"\b(NZD\/USD|NZDUSD|NZD-USD|NU)\b",
                @"\b(GBP\/JPY|GBPJPY|GBP-JPY|GJ)\b",
                @"\b(EUR\/JPY|EURJPY|EUR-JPY|EJ)\b",
                @"\b(EUR\/GBP|EURGBP|EUR-GBP|EG)\b",
                
                // Metals
                @"\b(GOLD|XAUUSD|XAU\/USD|XAU|AU)\b",
                @"\b(SILVER|XAGUSD|XAG\/USD|XAG|AG)\b",
                
                // Commodities
                @"\b(OIL|CRUDE|USOIL|WTI|BRENT|UKOIL)\b",
                @"\b(COPPER|XPTUSD|PLATINUM)\b",
                
                // Crypto
                @"\b(BITCOIN|BTC|BTCUSD|BTC\/USD)\b",
                @"\b(ETHEREUM|ETH|ETHUSD|ETH\/USD)\b",
                @"\b(RIPPLE|XRP|XRPUSD)\b",
                @"\b(LITECOIN|LTC|LTCUSD)\b",
                
                // Indices
                @"\b(US30|DOW|DJIA|DJ30)\b",
                @"\b(NAS100|NASDAQ|NDX|NAS)\b",
                @"\b(SPX500|SP500|SPX|S&P)\b",
                @"\b(GER30|DAX|DE30|GER40)\b",
                @"\b(UK100|FTSE|UKX|FTSE100)\b",
                @"\b(JPN225|NIKKEI|N225|NK)\b",
                @"\b(AUS200|ASX200|AU200)\b",
                @"\b(HK50|HSI|HANG.SENG)\b"
            };

            foreach (var pattern in symbolPatterns)
            {
                var match = Regex.Match(text, pattern);
                if (match.Success)
                {
                    signalData.OriginalSymbol = NormalizeSymbol(match.Value);
                    signalData.Symbol = signalData.OriginalSymbol;
                    OnDebugMessage($"Symbol detected: {signalData.OriginalSymbol}");
                    break;
                }
            }

            if (string.IsNullOrEmpty(signalData.Symbol))
            {
                // Try to extract symbol after BUY/SELL - more flexible
                var symbolAfterDirection = Regex.Match(text,
                    $@"\b{signalData.Direction}\s+([A-Z]{{2,8}}(?:\/[A-Z]{{3}}|[A-Z]{{0,3}})?)\b");
                if (symbolAfterDirection.Success)
                {
                    signalData.OriginalSymbol = NormalizeSymbol(symbolAfterDirection.Groups[1].Value);
                    signalData.Symbol = signalData.OriginalSymbol;
                    OnDebugMessage($"Symbol detected after direction: {signalData.OriginalSymbol}");
                }
            }

            if (string.IsNullOrEmpty(signalData.Symbol))
            {
                OnDebugMessage("No symbol found in message");
                return null;
            }

            // Extract Stop Loss - improved patterns
            var slPatterns = new[]
            {
                @"SL\s*[:=@]?\s*(\d+\.?\d*)",
                @"STOP\s*LOSS\s*[:=@]?\s*(\d+\.?\d*)",
                @"STOPLOSS\s*[:=@]?\s*(\d+\.?\d*)",
                @"S/L\s*[:=@]?\s*(\d+\.?\d*)",
                @"STOP\s*[:=@]?\s*(\d+\.?\d*)"
            };

            foreach (var pattern in slPatterns)
            {
                var match = Regex.Match(text, pattern);
                if (match.Success && double.TryParse(match.Groups[1].Value, out double sl))
                {
                    signalData.StopLoss = sl;
                    OnDebugMessage($"Stop Loss detected: {sl}");
                    break;
                }
            }

            // Extract Take Profits - improved patterns
            var tpPatterns = new[]
            {
                (@"TP\s*1?\s*[:=@]?\s*(\d+\.?\d*)", 1),
                (@"TP\s*2\s*[:=@]?\s*(\d+\.?\d*)", 2),
                (@"TP\s*3\s*[:=@]?\s*(\d+\.?\d*)", 3),
                (@"TAKE\s*PROFIT\s*1?\s*[:=@]?\s*(\d+\.?\d*)", 1),
                (@"T/P\s*1?\s*[:=@]?\s*(\d+\.?\d*)", 1),
                (@"TARGET\s*1?\s*[:=@]?\s*(\d+\.?\d*)", 1),
                (@"TARGET\s*2\s*[:=@]?\s*(\d+\.?\d*)", 2),
                (@"TARGET\s*3\s*[:=@]?\s*(\d+\.?\d*)", 3),
                (@"PROFIT\s*[:=@]?\s*(\d+\.?\d*)", 1)
            };

            foreach (var (pattern, level) in tpPatterns)
            {
                var match = Regex.Match(text, pattern);
                if (match.Success && double.TryParse(match.Groups[1].Value, out double tp))
                {
                    switch (level)
                    {
                        case 1:
                            signalData.TakeProfit1 = tp;
                            OnDebugMessage($"Take Profit 1 detected: {tp}");
                            break;
                        case 2:
                            signalData.TakeProfit2 = tp;
                            OnDebugMessage($"Take Profit 2 detected: {tp}");
                            break;
                        case 3:
                            signalData.TakeProfit3 = tp;
                            OnDebugMessage($"Take Profit 3 detected: {tp}");
                            break;
                    }
                }
            }

            // Extract Entry Price - improved patterns
            var entryPatterns = new[]
            {
                @"ENTRY\s*[:=@]?\s*(\d+\.?\d*)",
                @"PRICE\s*[:=@]?\s*(\d+\.?\d*)",
                @"AT\s*(\d+\.?\d*)",
                @"@\s*(\d+\.?\d*)",
                @"ENTER\s*[:=@]?\s*(\d+\.?\d*)"
            };

            foreach (var pattern in entryPatterns)
            {
                var match = Regex.Match(text, pattern);
                if (match.Success && double.TryParse(match.Groups[1].Value, out double entry))
                {
                    signalData.EntryPrice = entry;
                    OnDebugMessage($"Entry Price detected: {entry}");
                    break;
                }
            }

            OnDebugMessage($"Signal parsing completed: {signalData.Symbol} {signalData.Direction} SL:{signalData.StopLoss} TP1:{signalData.TakeProfit1}");
            return signalData;
        }

        /// <summary>
        /// Apply symbol mapping with proper error handling
        /// </summary>
        private void ApplySymbolMapping(ParsedSignalData parsedData)
        {
            try
            {
                OnDebugMessage($"Applying symbol mapping for: {parsedData.OriginalSymbol}");

                if (symbolMapping.Mappings.ContainsKey(parsedData.OriginalSymbol.ToUpper()))
                {
                    parsedData.Symbol = symbolMapping.Mappings[parsedData.OriginalSymbol.ToUpper()];
                    OnDebugMessage($"Symbol mapped: {parsedData.OriginalSymbol} -> {parsedData.Symbol}");
                }
                else
                {
                    parsedData.Symbol = parsedData.OriginalSymbol;
                    OnDebugMessage($"No mapping found, using original: {parsedData.Symbol}");
                }

                bool shouldSkip = symbolMapping.SkipPrefixSuffix.Contains(parsedData.Symbol.ToUpper());
                OnDebugMessage($"Skip prefix/suffix: {shouldSkip}");

                if (!shouldSkip)
                {
                    parsedData.FinalSymbol = symbolMapping.Prefix + parsedData.Symbol + symbolMapping.Suffix;
                    OnDebugMessage($"Applied prefix/suffix: {parsedData.Symbol} -> {parsedData.FinalSymbol}");
                }
                else
                {
                    parsedData.FinalSymbol = parsedData.Symbol;
                    OnDebugMessage($"Skipped prefix/suffix: {parsedData.FinalSymbol}");
                }

                if (symbolMapping.ExcludedSymbols.Contains(parsedData.FinalSymbol.ToUpper()) ||
                    symbolMapping.ExcludedSymbols.Contains(parsedData.OriginalSymbol.ToUpper()))
                {
                    OnDebugMessage($"Symbol excluded: {parsedData.OriginalSymbol}");
                    throw new InvalidOperationException($"Symbol {parsedData.OriginalSymbol} is excluded");
                }

                if (symbolMapping.AllowedSymbols.Count > 0)
                {
                    if (!symbolMapping.AllowedSymbols.Contains(parsedData.FinalSymbol.ToUpper()) &&
                        !symbolMapping.AllowedSymbols.Contains(parsedData.OriginalSymbol.ToUpper()))
                    {
                        OnDebugMessage($"Symbol not in whitelist: {parsedData.OriginalSymbol}");
                        throw new InvalidOperationException($"Symbol {parsedData.OriginalSymbol} not in whitelist");
                    }
                }

                OnDebugMessage($"Symbol mapping completed: {parsedData.OriginalSymbol} -> {parsedData.FinalSymbol}");
            }
            catch (Exception ex)
            {
                OnDebugMessage($"Symbol mapping failed: {ex.Message}");
                throw new InvalidOperationException($"Symbol mapping failed: {ex.Message}");
            }
        }
        /// <summary>
        /// Validate signal data
        private bool ValidateSignal(ParsedSignalData? parsedData)
        {
            if (parsedData == null)
            {
                OnDebugMessage("Validation failed: parsedData is null");
                return false;
            }

            if (string.IsNullOrEmpty(parsedData.Symbol))
            {
                OnDebugMessage("Validation failed: Symbol is empty");
                return false;
            }

            if (string.IsNullOrEmpty(parsedData.Direction))
            {
                OnDebugMessage("Validation failed: Direction is empty");
                return false;
            }

            if (string.IsNullOrEmpty(parsedData.FinalSymbol))
            {
                OnDebugMessage("Validation failed: FinalSymbol is empty");
                return false;
            }

            // Validate SL and TP logic
            if (parsedData.StopLoss > 0 && parsedData.TakeProfit1 > 0)
            {
                if (parsedData.Direction == "BUY")
                {
                    if (parsedData.StopLoss >= parsedData.TakeProfit1)
                    {
                        OnErrorOccurred($"Invalid BUY stops: SL ({parsedData.StopLoss}) >= TP ({parsedData.TakeProfit1})");
                        OnDebugMessage($"Validation failed: Invalid BUY stops - SL >= TP");
                        return false;
                    }
                }
                else if (parsedData.Direction == "SELL")
                {
                    if (parsedData.StopLoss <= parsedData.TakeProfit1)
                    {
                        OnErrorOccurred($"Invalid SELL stops: SL ({parsedData.StopLoss}) <= TP ({parsedData.TakeProfit1})");
                        OnDebugMessage($"Validation failed: Invalid SELL stops - SL <= TP");
                        return false;
                    }
                }
            }

            OnDebugMessage("Signal validation passed");
            return true;
        }


        public void CleanupProcessedSignals()
        {
            Task.Run(async () => await CleanupProcessedSignalsAsync()).Wait();
        }
        public void LoadSymbolMapping()
        {
            try
            {
                if (File.Exists("symbol_settings.json"))
                {
                    var json = File.ReadAllText("symbol_settings.json");
                    symbolMapping = JsonConvert.DeserializeObject<SymbolMapping>(json) ?? new SymbolMapping();
                }
                else
                {
                    symbolMapping = new SymbolMapping();
                }
            }
            catch
            {
                symbolMapping = new SymbolMapping();
            }
        }
        public int GetProcessedSignalsCount()
        {
            // Return the count of processed signals
            return processedSignals.Count;
        }

        public void SaveSymbolMapping()
        {
            try
            {
                var json = JsonConvert.SerializeObject(symbolMapping, Formatting.Indented);
                File.WriteAllText("symbol_settings.json", json);
            }
            catch (Exception ex)
            {
                OnErrorOccurred($"Failed to save symbol mapping: {ex.Message}");
            }
        }

        public void LoadEASettings()
        {
            try
            {
                if (File.Exists("ea_settings.json"))
                {
                    var json = File.ReadAllText("ea_settings.json");
                    eaSettings = JsonConvert.DeserializeObject<EASettings>(json) ?? new EASettings();
                }
                else
                {
                    eaSettings = new EASettings();
                }
            }
            catch
            {
                eaSettings = new EASettings();
            }
        }

        public void SaveEASettings()
        {
            try
            {
                var json = JsonConvert.SerializeObject(eaSettings, Formatting.Indented);
                File.WriteAllText("ea_settings.json", json);
            }
            catch (Exception ex)
            {
                OnErrorOccurred($"Failed to save EA settings: {ex.Message}");
            }
        }

        private void LoadSignalsHistory()
        {
            try
            {
                if (File.Exists(signalsHistoryFile))
                {
                    var json = File.ReadAllText(signalsHistoryFile);
                    processedSignals = JsonConvert.DeserializeObject<List<ProcessedSignal>>(json) ?? new List<ProcessedSignal>();
                }
                else
                {
                    processedSignals = new List<ProcessedSignal>();
                }
            }
            catch
            {
                processedSignals = new List<ProcessedSignal>();
            }
        }
        private void SaveSignalsHistory()
        {
            try
            {
                if (processedSignals.Count > 1000)
                {
                    processedSignals = processedSignals
                        .OrderByDescending(s => s.DateTime)
                        .Take(1000)
                        .ToList();
                }

                var json = JsonConvert.SerializeObject(processedSignals, Formatting.Indented);
                File.WriteAllText(signalsHistoryFile, json);
            }
            catch (Exception ex)
            {
                OnErrorOccurred($"Failed to save signals history: {ex.Message}");
            }
        }


        public void UpdateSymbolMapping(SymbolMapping newMapping)
        {
            symbolMapping = newMapping;
            SaveSymbolMapping();
        }
        public SymbolMapping GetSymbolMapping()
        {
            return symbolMapping;
        }

        public void UpdateEASettings(EASettings newSettings)
        {
            eaSettings = newSettings;
            SaveEASettings();
        }

        public EASettings GetEASettings()
        {
            return eaSettings;
        }

        public List<ProcessedSignal> GetSignalsHistory()
        {
            lock (processedSignals)
            {
                return processedSignals.ToList();
            }
        }


        protected virtual void OnSignalProcessed(ProcessedSignal signal)
        {
            SignalProcessed?.Invoke(this, signal);
        }

        protected virtual void OnErrorOccurred(string error)
        {
            ErrorOccurred?.Invoke(this, error);
        }

        protected virtual void OnDebugMessage(string message)
        {
            DebugMessage?.Invoke(this, message);
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");
        }

        public void ForceSaveHistory()
        {
            try
            {
                SaveSignalsHistory();
                OnDebugMessage("Signal history saved manually");
            }
            catch (Exception ex)
            {
                OnErrorOccurred($"Failed to force save history: {ex.Message}");
            }
        }
        public void ClearSignalsHistory()
        {
            lock (processedSignals)
            {
                processedSignals.Clear();
            }
            SaveSignalsHistory();
            OnDebugMessage("Signal history cleared");
        }
        public void Dispose()
        {
            try
            {
                // Stop the background processor
                cancellationTokenSource?.Cancel();

                // Process any remaining signals in the queue
                if (signalQueue != null && signalQueue.Count > 0)
                {
                    OnDebugMessage($"Processing {signalQueue.Count} remaining signals before disposal...");

                    while (signalQueue.TryTake(out var signal, 100))
                    {
                        try
                        {
                            WriteSignalToEAFileAsync(signal).Wait(5000);
                        }
                        catch (Exception ex)
                        {
                            OnErrorOccurred($"Error processing final signal: {ex.Message}");
                        }
                    }
                }

                // Wait for background processor to complete
                if (backgroundProcessorTask != null)
                {
                    try
                    {
                        backgroundProcessorTask.Wait(5000);
                    }
                    catch (AggregateException) { }
                }

                // Save signals history before disposing
                SaveSignalsHistory();

                // Cleanup message hashes
                CleanupOldMessageHashes();

                // Dispose resources
                cancellationTokenSource?.Dispose();
                signalQueue?.Dispose();
                fileWriteSemaphore?.Dispose();

                OnDebugMessage("SignalProcessingService disposed successfully");
            }
            catch (Exception ex)
            {
                OnErrorOccurred($"Error during disposal: {ex.Message}");
            }
        }

        ~SignalProcessingService()
        {
            Dispose();
        }

    }
}