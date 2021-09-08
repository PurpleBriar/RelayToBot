using Microsoft.Azure.Relay;
using Microsoft.Extensions.Configuration;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace RelayToBot
{
    class Program
    {
        private static bool KeepRunning = true;
        private static IConfiguration AppConfig;
        private static IConfiguration UserConfig;
        private static string LeftSectionFiller;
        private static string MidSectionFiller;
        private static string ConnectionName;
        private static string TargetHttpRelay;
        private static bool IsHttpRelayMode;
        private static string ConnectionStatus = "offline";

        static void Main(string[] args)
        {
            Console.CancelKeyPress += (sender, eventArgs) => {
                // call methods to clean up
                eventArgs.Cancel = true;
                Program.KeepRunning = false;
            };

            AppConfig = new ConfigurationBuilder()
                .SetBasePath(AppDomain.CurrentDomain.BaseDirectory)
                .AddJsonFile("appsettings.json", false, true)
                .AddEnvironmentVariables()
                .Build();

            ShowHeader(AppConfig);

            // Set format
            Logger.MaxRows = int.Parse(AppConfig["App:MessageRows"]);
            Logger.LeftPad = int.Parse(AppConfig["App:LeftPad"]);
            Logger.MidPad = int.Parse(AppConfig["App:MidPad"]);
            LeftSectionFiller = string.Empty.PadRight(Logger.LeftPad, ' ');
            MidSectionFiller = string.Empty.PadRight(Logger.MidPad, ' ');

            UserConfig = new ConfigurationBuilder()
                .AddJsonFile("RelayToBot.json", false, true) 
                .Build();

            IsHttpRelayMode = UserConfig["Relay:Mode"].Equals("http", StringComparison.CurrentCultureIgnoreCase);

            var configHeader = IsHttpRelayMode ? "Http" : "WebSocket";

            var relayNamespace = $"{UserConfig[$"{configHeader}:Namespace"]}.servicebus.windows.net";
            var keyName = UserConfig[$"{configHeader}:PolicyName"];
            var key = UserConfig[$"{configHeader}:PolicyKey"];

            ConnectionName = UserConfig[$"{configHeader}:ConnectionName"];
            TargetHttpRelay = UserConfig[$"{configHeader}:TargetServiceAddress"];

            try
            {
                while (Program.KeepRunning)
                {
                    ShowAll();
                    // Do your work in here, in small chunks.
                    // If you literally just want to wait until ctrl-c,
                    // not doing anything, see the answer using set-reset events.
                    if (IsHttpRelayMode)
                    {
                        // Create the Http hybrid proxy listener
                        var httpRelayListener = new HttpListener(
                            relayNamespace,
                            ConnectionName,
                            keyName,
                            key,
                            ConnectionEventHandler,
                            new CancellationTokenSource());

                        // Opening the listener establishes the control channel to
                        // the Azure Relay service. The control channel is continuously
                        // maintained, and is reestablished when connectivity is disrupted.
                        Program.KeepRunning = RunHttpRelayAsync(httpRelayListener).GetAwaiter().GetResult();
                    }
                }
            }
            catch (Exception e)
            {
                ShowError(e.Message);
            }
        }

        /// <summary>
        /// RunHttpRelayAsync
        /// </summary>
        /// <param name="httpRelayListener"></param>
        /// <returns></returns>
        static async Task<bool> RunHttpRelayAsync(HttpListener httpRelayListener)
        {
            await httpRelayListener.OpenAsync(ProcessHttpMessagesHandler);

            // Start a new thread that will continuously read the console.
            await httpRelayListener.ListenAsync();

            // Close the connection
            await httpRelayListener.CloseAsync();

            // Return false, if the cancellation was requested, otherwise - true
            return !httpRelayListener.CTS.IsCancellationRequested;
        }


        /// <summary>
        /// Listener Response Handler
        /// </summary>
        /// <param name="context"></param>
        static async void ProcessHttpMessagesHandler(RelayedHttpListenerContext context)
        {
            var startTimeUtc = DateTime.UtcNow;

            try
            {
                // Send the request message to the target listener
                var requestMessage = await HttpListener.CreateHttpRequestMessageAsync(context, ConnectionName);
                var responseMessage = await SendHttpRequestAsync(requestMessage);
                Logger.LogRequest(requestMessage.Method.Method, requestMessage.RequestUri.LocalPath, $"\u001b[32m {responseMessage.StatusCode} \u001b[0m", $"Forwarded to {TargetHttpRelay}.", ShowAll);
            }
            catch (RelayException re)
            {
                Logger.LogRequest("Http", ConnectionName, $"\u001b[31m {System.Net.HttpStatusCode.ServiceUnavailable} \u001b[0m", re.Message, ShowAll);
            }
            catch (Exception e)
            {
                Logger.LogRequest("Http", ConnectionName, $"\u001b[31m {e.GetType().Name} \u001b[0m", e.Message, ShowAll);
                HttpListener.SendErrorResponse(e, context);
            }
            finally
            {
                Logger.LogPerformanceMetrics(startTimeUtc);
                // The context MUST be closed here
                await context.Response.CloseAsync();
            }
        }



        /// <summary>
        /// Creates and sends the Stream message over Http Relay connection
        /// </summary>
        /// <param name="requestMessage"></param>
        /// <returns></returns>
        static async Task<HttpResponseMessage> SendHttpRequestAsync(HttpRequestMessage requestMessage)
        {
            try
            {
                // Send the request message via Http
                using (var httpClient = new HttpClient { BaseAddress = new Uri(TargetHttpRelay, UriKind.RelativeOrAbsolute) })
                {
                    httpClient.DefaultRequestHeaders.ExpectContinue = false;
                    return await httpClient.SendAsync(requestMessage);
                }
            }
            catch (Exception e)
            {
                Console.Error.WriteLine(e);
                throw;
            }
        }

        static void ShowAll()
        {
            Console.Clear();
            ShowHeader(AppConfig);
            ShowConfiguration(UserConfig);
            ShowRequestsHeader();

            List<string> logs = new List<string>();
            logs.AddRange(Logger.Logs);
            logs.Reverse();

            foreach (var message in logs)
            {
                Console.WriteLine(message);
            }
        }

        static void ShowHeader(IConfiguration config)
        {
            var appName = config["App:Name"];
            var author = config["App:Author"];
            var version = config["App:Version"];

            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.Write(appName);
            Console.ForegroundColor = ConsoleColor.White;
            Console.Write(" by ");
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine(author);
            Console.WriteLine($"Version: {version}");
            Console.ResetColor();
            Console.WriteLine("\n\r\n\r");
        }

        static void ShowError(string message)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine(message);
            Console.WriteLine("");
            Console.ResetColor();
        }

        static void ShowConfiguration(IConfiguration config)
        {
            var relayNamespace = $"sb://{config["Relay:Namespace"]}.servicebus.windows.net";
            var IsHttpRelayMode = config["Relay:Mode"].Equals("http", StringComparison.CurrentCultureIgnoreCase);

            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine($"{LeftSectionFiller}{MidSectionFiller}(Ctrl+C to quit)");
            Console.ForegroundColor = ConsoleColor.Green;
            var title = "Session Status";
            var filler = string.Empty.PadRight(LeftSectionFiller.Length - title.Length > 0 ? LeftSectionFiller.Length - title.Length : 0);
            Console.WriteLine($"{title}{filler}{MidSectionFiller}{ConnectionStatus}");

            Console.ForegroundColor = ConsoleColor.White;
            title = "Azure Relay Namespace";
            filler = string.Empty.PadRight(LeftSectionFiller.Length - title.Length > 0 ? LeftSectionFiller.Length - title.Length : 0);
            Console.WriteLine($"{title}{filler}{MidSectionFiller}{relayNamespace}");

            title = IsHttpRelayMode ? "Http Forwarding" : "Websocket Forwarding";
            filler = string.Empty.PadRight(LeftSectionFiller.Length - title.Length > 0 ? LeftSectionFiller.Length - title.Length : 0);
            Console.WriteLine($"{title}{filler}{MidSectionFiller}{relayNamespace}/{ConnectionName} {(char)29} {TargetHttpRelay}");
        }

        /// <summary>
        /// Show Request Logs Header
        /// </summary>
        static void ShowRequestsHeader()
        {
            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine("\n\r\n\r");
            Console.WriteLine(IsHttpRelayMode ? "HTTP Requests" : "Websocket Requests");
            Console.WriteLine("___________________");
            Console.WriteLine("\n\r");
        }


        /// <summary>
        /// Outputs the listener's event messages
        /// </summary>
        /// <param name="eventMessage"></param>
        static void ConnectionEventHandler(string eventMessage)
        {
            ConnectionStatus = eventMessage;
            ShowAll();
        }
    }
}
