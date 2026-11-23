using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SyncTrayzor.Services.ExplorerIntegration;
using Xunit;

namespace SyncTrayzor.Tests
{
    public class ExplorerIntegrationServerTests
    {
        private readonly FakeStatus status = new();
        private readonly FakeCommands commands = new();
        private readonly ExplorerIntegrationServer server;

        public ExplorerIntegrationServerTests()
        {
            server = new ExplorerIntegrationServer(status, commands);
        }

        [Fact]
        public async Task Roots()
        {
            Assert.Equal("C:\\Sync\nE:\\", await server.HandleRequestAsync("ROOTS", CancellationToken.None));
        }

        [Fact]
        public async Task State()
        {
            Assert.Equal("2", await server.HandleRequestAsync("STATE\nC:\\Sync\\a.txt", CancellationToken.None));
            Assert.Equal("C:\\Sync\\a.txt", status.LastPath);
        }

        [Fact]
        public async Task StateNeedsExactlyOnePath()
        {
            Assert.Equal("ERR", await server.HandleRequestAsync("STATE", CancellationToken.None));
            Assert.Equal("ERR", await server.HandleRequestAsync("STATE\na\nb", CancellationToken.None));
        }

        [Theory]
        [InlineData("MENU", false)]
        [InlineData("MENU_BACKGROUND", true)]
        public async Task Menu(string command, bool isBackground)
        {
            var response = await server.HandleRequestAsync(command + "\nC:\\Sync\\a\r\nC:\\Sync\\b\n", CancellationToken.None);

            Assert.Equal("synctrayzor.open\tOpen SyncTrayzor", response);
            Assert.Equal(["C:\\Sync\\a", "C:\\Sync\\b"], commands.LastPaths);
            Assert.Equal(isBackground, commands.LastIsBackground);
        }

        [Fact]
        public async Task Invoke()
        {
            Assert.Equal("OK", await server.HandleRequestAsync("INVOKE\nsynctrayzor.rescan\nC:\\Sync\\a", CancellationToken.None));
            Assert.Equal("synctrayzor.rescan", commands.LastVerb);
            Assert.Equal(["C:\\Sync\\a"], commands.LastPaths);
        }

        [Fact]
        public async Task InvokeNeedsAPath()
        {
            Assert.Equal("ERR", await server.HandleRequestAsync("INVOKE\nsynctrayzor.rescan", CancellationToken.None));
            Assert.Null(commands.LastVerb);
        }

        [Fact]
        public async Task UnknownCommand()
        {
            Assert.Equal("ERR", await server.HandleRequestAsync("DELETE\nC:\\", CancellationToken.None));
        }

        private sealed class FakeStatus : IExplorerStatusService
        {
            public string LastPath { get; private set; }
            public bool IsEnabled { get; set; }
            public IReadOnlyList<string> Roots => ["C:\\Sync", "E:\\"];
            public bool TryResolve(string path, out FolderPath folderPath) => throw new NotImplementedException();
            public bool IsFolderPaused(string folderId) => false;

            public ExplorerItemState GetState(string path)
            {
                LastPath = path;
                return ExplorerItemState.Syncing;
            }

            public bool? IsIgnored(FolderPath path) => false;
            public Task<IgnoreMatcher> RefreshIgnoresAsync(string folderId, CancellationToken cancellationToken) => Task.FromResult(IgnoreMatcher.Empty);
            public void Dispose() { }
        }

        private sealed class FakeCommands : IExplorerCommandHandler
        {
            public IReadOnlyList<string> LastPaths { get; private set; }
            public bool LastIsBackground { get; private set; }
            public string LastVerb { get; private set; }

            public Task<IReadOnlyList<ExplorerMenuItem>> GetMenuAsync(IReadOnlyList<string> paths, bool isBackground, CancellationToken cancellationToken)
            {
                LastPaths = paths;
                LastIsBackground = isBackground;
                return Task.FromResult<IReadOnlyList<ExplorerMenuItem>>([new ExplorerMenuItem(ExplorerCommandHandler.VerbOpen, "Open SyncTrayzor")]);
            }

            public void Invoke(string verb, IReadOnlyList<string> paths)
            {
                LastVerb = verb;
                LastPaths = paths;
            }
        }
    }
}
