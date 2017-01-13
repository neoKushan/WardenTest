using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Warden.Core;
using Warden.Integrations.Cachet;
using Warden.Integrations.HttpApi;
using Warden.Integrations.MsSql;
using Warden.Utils;
using Warden.Watchers;
using Warden.Watchers.Disk;
using Warden.Watchers.MongoDb;
using Warden.Watchers.MsSql;
using Warden.Watchers.Performance;
using Warden.Watchers.Process;
using Warden.Watchers.Redis;
using Warden.Watchers.Server;
using Warden.Watchers.Web;

namespace Warden.WindowsService
{
    public class WardenService
    {
        private static readonly IWarden Warden = ConfigureWarden();

        public async Task StartAsync()
        {
            Console.WriteLine("Warden service has been started.");
            await Warden.StartAsync();
        }

        public async Task PauseAsync()
        {
            await Warden.PauseAsync();
            Console.WriteLine("Warden service has been paused.");
        }

        public async Task StopAsync()
        {
            await Warden.StopAsync();
            Console.WriteLine("Warden service has been stopped.");
        }

        private static IWarden ConfigureWarden()
        {
            var wardenConfiguration = WardenConfiguration
                .Create()
                .AddDiskWatcher(cfg =>
                {
                    cfg.WithFilesToCheck(@"D:\Test\File1.txt", @"D:\Test\File2.txt")
                        .WithPartitionsToCheck("D", @"E:\")
                        .WithDirectoriesToCheck(@"D:\Test");
                })
                .AddMongoDbWatcher("mongodb://localhost:27017", "MyDatabase", cfg =>
                {
                    cfg.WithQuery("Users", "{\"name\": \"admin\"}")
                        .EnsureThat(users => users.Any(user => user.role == "admin"));
                })
                .AddMsSqlWatcher(@"Data Source=.\sqlexpress;Initial Catalog=MyDatabase;Integrated Security=True",
                    cfg =>
                    {
                        cfg.WithQuery("select * from users where id = @id", new Dictionary<string, object> {["id"] = 1})
                            .EnsureThat(users => users.Any(user => user.Name == "admin"));
                    })
                .AddPerformanceWatcher(cfg => cfg.EnsureThat(usage => usage.Cpu < 50 && usage.Ram < 5000),
                    hooks =>
                        hooks.OnCompleted(result => Console.WriteLine(result.WatcherCheckResult.Description)))
                .AddProcessWatcher("mongod")
                .AddRedisWatcher("localhost", 1, cfg =>
                {
                    cfg.WithQuery("get test")
                        .EnsureThat(results => results.Any(x => x == "test-value"));
                })
                .AddWebWatcher("http://httpstat.us/200", hooks =>
                {
                    hooks.OnStartAsync(check => WebsiteHookOnStartAsync(check))
                        .OnSuccessAsync(check => WebsiteHookOnSuccessAsync(check))
                        .OnCompletedAsync(check => WebsiteHookOnCompletedAsync(check))
                        .OnFailureAsync(check => WebsiteHookOnFailureAsync(check));
                }, interval: TimeSpan.FromMilliseconds(1000))
                .AddServerWatcher("www.google.pl", 80)
                .AddWebWatcher("http://httpstat.us/200", HttpRequest.Post("users", new {name = "test"},
                    headers: new Dictionary<string, string>
                    {
                        ["User-Agent"] = "Warden",
                        ["Authorization"] = "Token MyBase64EncodedString"
                    }), cfg => cfg.EnsureThat(response => response.Headers.Any())
                )
                .IntegrateWithMsSql(@"Data Source=.\sqlexpress;Initial Catalog=MyDatabase;Integrated Security=True")
                //Set proper API key or credentials.
                //.IntegrateWithSendGrid("api-key", "noreply@system.com", cfg =>
                //{
                //    cfg.WithDefaultSubject("Monitoring status")
                //        .WithDefaultReceivers("admin@system.com");
                //})
                //.SetAggregatedWatcherHooks((hooks, integrations) =>
                //{
                //    hooks.OnFirstFailureAsync(result =>
                //        integrations.SendGrid().SendEmailAsync("Monitoring errors have occured."))
                //        .OnFirstSuccessAsync(results =>
                //            integrations.SendGrid().SendEmailAsync("Everything is up and running again!"));
                //})
                //Set proper URL of the Warden Web API
                .IntegrateWithHttpApi("http://localhost:11223/api",
                    "yroWbGkozycDLMI7+Jkyw0FzJv/O6xHzhR8+DcKTNEQECZHFBFmBbYCKJ2wiHYI=",
                    "20afbd7c-f803-4a2d-be64-640776930930")
                //New Warden API integration
                //.IntegrateWithHttpApi("http://localhost:20899",
                //    "9l5G2m695GOUvcfnIVIDX/QptT/2U30QFa95cdfmNMzBJirqF6Epcr2h",
                //    "WzoJnQI0oEq5tmu9irTKXg", "R8wO5nNnXU6kFojXyiG1GA")
                //Set proper Slack webhook URL
                //.IntegrateWithSlack("https://hooks.slack.com/services/XXX/YYY/ZZZ")
                //Set proper URL of Cachet API and access token or username & password
                //.IntegrateWithCachet("http://localhost/api/v1", "XYZ")
                .SetGlobalWatcherHooks((hooks, integrations) =>
                {
                    hooks.OnStart(check => GlobalHookOnStart(check))
                        .OnFailure(result => GlobalHookOnFailure(result))
                        .OnSuccess(result => GlobalHookOnSuccess(result))
                        //.OnCompletedAsync(result => GlobalHookOnCompletedPostToNewApiAsync(result, integrations.HttpApi()))
                        .OnCompleted(result => GlobalHookOnCompleted(result));
                })
                .SetHooks((hooks, integrations) =>
                {
                    hooks.OnIterationCompleted(iteration => OnIterationCompleted(iteration))
                        .OnIterationCompletedAsync(iteration => OnIterationCompletedCachetAsync(iteration, integrations.Cachet()))
                        //.OnIterationCompletedAsync(iteration =>
                        //    integrations.Slack().SendMessageAsync($"Iteration {iteration.Ordinal} has completed."))
                        .OnIterationCompletedAsync(iteration => integrations.HttpApi()
                            .PostIterationToWardenPanelAsync(iteration))
                        .OnError(exception => Console.WriteLine(exception))
                        .OnIterationCompletedAsync(
                            iteration => OnIterationCompletedMsSqlAsync(iteration, integrations.MsSql()));
                })
                .WithConsoleLogger(minLevel: WardenLoggerLevel.Info, useColors: true)
                .Build();

            return WardenInstance.Create(wardenConfiguration);
        }

        private static async Task WebsiteHookOnStartAsync(IWatcherCheck check)
        {
            Console.WriteLine($"Invoking the hook OnStartAsync() by watcher: '{check.WatcherName}'.");
            await Task.FromResult(true);
        }

        private static async Task WebsiteHookOnSuccessAsync(IWardenCheckResult check)
        {
            var webWatcherCheckResult = (WebWatcherCheckResult) check.WatcherCheckResult;
            Console.WriteLine("Invoking the hook OnSuccessAsync() " +
                              $"by watcher: '{webWatcherCheckResult.WatcherName}'.");
            await Task.FromResult(true);
        }

        private static async Task WebsiteHookOnCompletedAsync(IWardenCheckResult check)
        {
            Console.WriteLine("Invoking the hook OnCompletedAsync() " +
                              $"by watcher: '{check.WatcherCheckResult.WatcherName}'.");
            await Task.FromResult(true);
        }

        private static async Task WebsiteHookOnFailureAsync(IWardenCheckResult check)
        {
            Console.WriteLine("Invoking the hook OnFailureAsync() " +
                              $"by watcher: '{check.WatcherCheckResult.WatcherName}'.");
            await Task.FromResult(true);
        }

        private static void GlobalHookOnStart(IWatcherCheck check)
        {
            Console.WriteLine("Invoking the global hook OnStart() " +
                              $"by watcher: '{check.WatcherName}'.");
        }

        private static void GlobalHookOnSuccess(IWardenCheckResult check)
        {
            Console.WriteLine("Invoking the global hook OnSuccess() " +
                              $"by watcher: '{check.WatcherCheckResult.WatcherName}'.");
        }

        private static void GlobalHookOnCompleted(IWardenCheckResult check)
        {
            Console.WriteLine("Invoking the global hook OnCompleted() " +
                              $"by watcher: '{check.WatcherCheckResult.WatcherName}'.");
        }

        private static async Task GlobalHookOnCompletedPostToNewApiAsync(IWardenCheckResult check,
            HttpApiIntegration httpApiIntegration)
        {
            await httpApiIntegration.PostCheckResultToWardenPanelAsync(check);
        }

        private static void GlobalHookOnFailure(IWardenCheckResult check)
        {
            Console.WriteLine("Invoking the global hook OnFailure() " +
                              $"by watcher: '{check.WatcherCheckResult.WatcherName}'.");
        }

        private static void OnIterationCompleted(IWardenIteration wardenIteration)
        {
            var newLine = Environment.NewLine;
            Console.WriteLine($"{wardenIteration.WardenName} iteration {wardenIteration.Ordinal} has completed.");
            foreach (var result in wardenIteration.Results)
            {
                Console.WriteLine($"Watcher: '{result.WatcherCheckResult.WatcherName}'{newLine}" +
                                  $"Description: {result.WatcherCheckResult.Description}{newLine}" +
                                  $"Is valid: {result.IsValid}{newLine}" +
                                  $"Started at: {result.StartedAt}{newLine}" +
                                  $"Completed at: {result.CompletedAt}{newLine}" +
                                  $"Execution time: {result.ExecutionTime}{newLine}");
            }
        }

        private static async Task OnIterationCompletedMsSqlAsync(IWardenIteration wardenIteration,
            MsSqlIntegration integration)
        {
            await integration.SaveIterationAsync(wardenIteration);
        }

        private static async Task OnIterationCompletedCachetAsync(IWardenIteration wardenIteration,
            CachetIntegration cachet)
        {
            await cachet.SaveIterationAsync(wardenIteration);
        }
    }
}