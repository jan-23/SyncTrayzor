using NLog;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SyncTrayzor.Services.ExplorerIntegration
{
    /// <summary>
    /// Named pipe server which answers the shell extension (SyncTrayzor.ShellExtension.dll) loaded in Explorer.
    /// </summary>
    /// <remarks>
    /// Protocol: one request and one response per connection, each a single pipe message of UTF-16LE text.
    /// The first line of a request is the command, followed by its arguments, one per line:
    /// <list type="bullet">
    ///   <item>ROOTS: returns the root paths of all Syncthing folders, one per line</item>
    ///   <item>STATE path: returns the <see cref="ExplorerItemState"/> of the path as a number</item>
    ///   <item>MENU path...: returns "verb\ttext" lines for the SyncTrayzor submenu of selected items (nothing: no menu)</item>
    ///   <item>MENU_BACKGROUND path: the same, for the background of an open folder</item>
    ///   <item>INVOKE verb path...: starts the command and returns "OK"</item>
    /// </list>
    /// The pipe only accepts connections from the current user (PipeOptions.CurrentUserOnly), and connections from
    /// other machines are rejected explicitly.
    /// </remarks>
    public sealed class ExplorerIntegrationServer : IDisposable
    {
        private static readonly Logger logger = LogManager.GetCurrentClassLogger();

        // Explorer queries from many threads at once, so serve several connections in parallel
        private const int listenerCount = 4;
        private const int maxRequestBytes = 1024 * 1024;
        private static readonly TimeSpan requestTimeout = TimeSpan.FromSeconds(5);

        private readonly IExplorerStatusService statusService;
        private readonly IExplorerCommandHandler commandHandler;
        private CancellationTokenSource cts;

        public static string PipeName { get; } = "SyncTrayzor.Explorer." + WindowsIdentity.GetCurrent().User?.Value;

        public ExplorerIntegrationServer(IExplorerStatusService statusService, IExplorerCommandHandler commandHandler)
        {
            this.statusService = statusService;
            this.commandHandler = commandHandler;
        }

        public void Start()
        {
            if (cts != null)
                return;

            logger.Info("Starting Explorer integration server on pipe {0}", PipeName);
            cts = new CancellationTokenSource();
            var token = cts.Token;
            for (int i = 0; i < listenerCount; i++)
                Task.Run(() => ListenAsync(token));
        }

        public void Stop()
        {
            if (cts == null)
                return;

            logger.Info("Stopping Explorer integration server");
            cts.Cancel();
            cts.Dispose();
            cts = null;
        }

        private async Task ListenAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    using var pipe = new NamedPipeServerStream(
                        PipeName,
                        PipeDirection.InOut,
                        NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Message,
                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

                    await pipe.WaitForConnectionAsync(cancellationToken);
                    if (!IsLocalClient(pipe))
                    {
                        logger.Warn("Rejected a connection to the Explorer integration pipe from another machine");
                        continue;
                    }

                    using var requestCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    requestCts.CancelAfter(requestTimeout);
                    await HandleConnectionAsync(pipe, requestCts.Token);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception e) when (e is IOException or OperationCanceledException)
                {
                    // Client went away or was too slow. Not our problem.
                    logger.Trace(e, "Explorer integration client disconnected");
                }
                catch (Exception e)
                {
                    logger.Error(e, "Explorer integration server failed");
                    // Don't spin if something is persistently broken
                    await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
                }
            }
        }

        private async Task HandleConnectionAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
        {
            var request = await ReadMessageAsync(pipe, cancellationToken);
            if (request == null)
                return;

            var response = await HandleRequestAsync(request, cancellationToken);
            var responseBytes = Encoding.Unicode.GetBytes(response);
            await pipe.WriteAsync(responseBytes, cancellationToken);
            await pipe.FlushAsync(cancellationToken);
        }

        private static async Task<string> ReadMessageAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
        {
            using var buffer = new MemoryStream();
            var chunk = new byte[4096];
            do
            {
                int read = await pipe.ReadAsync(chunk, cancellationToken);
                if (read == 0)
                    break;
                buffer.Write(chunk, 0, read);
                if (buffer.Length > maxRequestBytes)
                {
                    logger.Warn("Ignoring oversized request from the shell extension");
                    return null;
                }
            } while (!pipe.IsMessageComplete);

            return Encoding.Unicode.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
        }

        public async Task<string> HandleRequestAsync(string request, CancellationToken cancellationToken)
        {
            var lines = request.Split('\n').Select(x => x.TrimEnd('\r')).ToList();
            var command = lines[0];
            var args = lines.Skip(1).Where(x => x.Length > 0).ToList();

            switch (command)
            {
                case "ROOTS":
                    return String.Join("\n", statusService.Roots);

                case "STATE":
                    if (args.Count != 1)
                        return "ERR";
                    return ((int)statusService.GetState(args[0])).ToString();

                case "MENU":
                case "MENU_BACKGROUND":
                    var items = await commandHandler.GetMenuAsync(args, command == "MENU_BACKGROUND", cancellationToken);
                    return String.Join("\n", items.Select(x => $"{x.Verb}\t{x.Text}"));

                case "INVOKE":
                    if (args.Count < 2)
                        return "ERR";
                    commandHandler.Invoke(args[0], args.Skip(1).ToList());
                    return "OK";

                default:
                    logger.Warn("Unknown request from the shell extension: {0}", command);
                    return "ERR";
            }
        }

        // A client on another machine connects through the SMB server, which reports the client's computer name.
        // For local clients there's none, and the call fails.
        private static bool IsLocalClient(NamedPipeServerStream pipe)
        {
            var name = new StringBuilder(256);
            return !GetNamedPipeClientComputerNameW(pipe.SafePipeHandle, name, (uint)name.Capacity * sizeof(char));
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetNamedPipeClientComputerNameW(SafePipeHandle pipe, StringBuilder clientComputerName, uint clientComputerNameLength);

        public void Dispose()
        {
            Stop();
        }
    }
}
