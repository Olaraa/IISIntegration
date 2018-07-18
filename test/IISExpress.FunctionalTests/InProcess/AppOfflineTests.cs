// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Server.IIS.FunctionalTests.Utilities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.IISIntegration.FunctionalTests;
using Microsoft.AspNetCore.Server.IntegrationTesting;
using Microsoft.AspNetCore.Testing;
using Microsoft.AspNetCore.Testing.xunit;
using Microsoft.Extensions.Logging;
using Xunit;
using Microsoft.AspNetCore.Server.IntegrationTesting.IIS;

namespace Microsoft.AspNetCore.Server.IIS.FunctionalTests.Inprocess
{
    public class AppOfflineTests : IISFunctionalTestBase
    {
        // TODO these will differ between IIS and IISExpress
        [ConditionalTheory]
        [InlineData(HostingModel.InProcess)]
        [InlineData(HostingModel.OutOfProcess)]
        public async Task AppOfflineDroppedWhileSiteIsDown_SiteReturns503(HostingModel hostingModel)
        {
            var deploymentResult = await DeployApp(hostingModel);

            AddAppOffline(deploymentResult.ContentRoot);

            await AssertAppOffline(deploymentResult);
            AssertFilesNotLocked(deploymentResult);
        }

        [ConditionalFact]
        public async Task AppOfflineDroppedWhileSiteFailedToStartInShim_AppOfflineServed_InProcess()
        {
            var deploymentResult = await DeployApp(HostingModel.InProcess);

            Helpers.ModifyAspNetCoreSectionInWebConfig(deploymentResult, "processPath", "nonexistent");

            var result = await deploymentResult.HttpClient.GetAsync("/");
            Assert.Equal(500, (int)result.StatusCode);
            Assert.Contains("500.0", await result.Content.ReadAsStringAsync());

            AddAppOffline(deploymentResult.DeploymentResult.ContentRoot);

            await AssertAppOffline(deploymentResult);
            AssertFilesNotLocked(deploymentResult);
        }

        [ConditionalFact]
        public async Task AppOfflineDroppedWhileSiteFailedToStartInRequestHandler_SiteStops_InProcess()
        {
            var deploymentResult = await DeployApp(HostingModel.InProcess);

            // Set file content to empty so it fails at runtime
            File.WriteAllText(Path.Combine(deploymentResult.DeploymentResult.ContentRoot, "Microsoft.AspNetCore.Server.IIS.dll"), "");

            var result = await deploymentResult.HttpClient.GetAsync("/");
            Assert.Equal(500, (int)result.StatusCode);
            Assert.Contains("500.30", await result.Content.ReadAsStringAsync());

            AddAppOffline(deploymentResult.DeploymentResult.ContentRoot);
            AssertStopsProcess(deploymentResult);
        }

        
        [ConditionalFact]
        public async Task AppOfflineDroppedWhileSiteFailedToStart_SiteStops_OutOfProcess()
        {
            var deploymentResult = await DeployApp(HostingModel.OutOfProcess);

            Helpers.ModifyAspNetCoreSectionInWebConfig(deploymentResult, "processPath", "nonexistent");

            var result = await deploymentResult.HttpClient.GetAsync("/");
            Assert.Equal(502, (int)result.StatusCode);

            AddAppOffline(deploymentResult.DeploymentResult.ContentRoot);
            await AssertAppOffline(deploymentResult);
            AssertFilesNotLocked(deploymentResult);
        }

        [ConditionalFact]
        public async Task AppOfflineDroppedWhileSiteStarting_SiteShutsDown_InProcess()
        {
            var deploymentResult = await DeployApp(HostingModel.InProcess);

            for (int i = 0; i < 10; i++)
            {
                
                // send first request and add app_offline while app is starting
                var runningTask = AssertAppOffline(deploymentResult);

                // This test tries to hit a race where we drop app_offline file while
                // in process application is starting, application start takes at least 400ms
                // so we back off for 100ms to allow request to reach request handler
                // Test itself is racy and can result in two scenarios
                //    1. ANCM detects app_offline before it starts the request - if AssertAppOffline succeeds we've hit it
                //    2. Intended scenario where app starts and then shuts down
                // In first case we remove app_offline and try again
                await Task.Delay(100);

                AddAppOffline(deploymentResult.ContentRoot);

                try
                {
                    await runningTask.TimeoutAfterDefault();

                    // if AssertAppOffline succeeded ANCM have picked up app_offline before starting the app
                    // try again
                    RemoveAppOffline(deploymentResult.ContentRoot);
                }
                catch
                {
                    AssertStopsProcess(deploymentResult);
                    return;
                }
            }

            Assert.True(false);
        }

        [ConditionalFact]
        public async Task AppOfflineDroppedWhileSiteRunning_SiteShutsDown_InProcess()
        {
            var deploymentResult = await AssertStarts(HostingModel.InProcess);

            AddAppOffline(deploymentResult.ContentRoot);

            AssertStopsProcess(deploymentResult);
        }

        [ConditionalFact]
        public async Task AppOfflineDroppedWhileSiteRunning_SiteShutsDown_OutOfProcess()
        {
            var deploymentResult = await AssertStarts(HostingModel.OutOfProcess);

            // Repeat dropping file and restarting multiple times
            for (int i = 0; i < 5; i++)
            {
                AddAppOffline(deploymentResult.ContentRoot);
                await AssertAppOffline(deploymentResult);
                RemoveAppOffline(deploymentResult.ContentRoot);
                await AssertRunning(deploymentResult);
            }

            AddAppOffline(deploymentResult.DeploymentResult.ContentRoot);
            await AssertAppOffline(deploymentResult);
            AssertFilesNotLocked(deploymentResult);
        }

        [ConditionalTheory]
        [InlineData(HostingModel.InProcess)]
        [InlineData(HostingModel.OutOfProcess)]
        public async Task AppOfflineDropped_CanRemoveAppOfflineAfterAddingAndSiteWorks(HostingModel hostingModel)
        {
            var deploymentResult = await DeployApp(hostingModel);

            AddAppOffline(deploymentResult.ContentRoot);

            await AssertAppOffline(deploymentResult);

            RemoveAppOffline(deploymentResult.ContentRoot);

            await AssertRunning(deploymentResult);
        }

        private async Task<IISDeploymentResult> DeployApp(HostingModel hostingModel = HostingModel.InProcess)
        {
            var deploymentParameters = Helpers.GetBaseDeploymentParameters(hostingModel: hostingModel, publish: true);

            return await DeployAsync(deploymentParameters);
        }

        private void AddAppOffline(string appPath, string content = "The app is offline.")
        {
            File.WriteAllText(Path.Combine(appPath, "app_offline.htm"), content);
        }

        private void RemoveAppOffline(string appPath)
        {
            RetryHelper.RetryOperation(
                () => File.Delete(Path.Combine(appPath, "app_offline.htm")),
                e => Logger.LogError($"Failed to remove app_offline : {e.Message}"),
                retryCount: 3,
                retryDelayMilliseconds: 100);
        }

        private async Task AssertAppOffline(IISDeploymentResult deploymentResult, string expectedResponse = "The app is offline.")
        {
            var response = await deploymentResult.HttpClient.GetAsync("HelloWorld");

            for (var i = 0; response.IsSuccessStatusCode && i < 5; i++)
            {
                // Keep retrying until app_offline is present.
                response = await deploymentResult.HttpClient.GetAsync("HelloWorld");
            }

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

            Assert.Equal(expectedResponse, await response.Content.ReadAsStringAsync());
        }

        private void AssertStopsProcess(IISDeploymentResult deploymentResult)
        {
            var hostShutdownToken = deploymentResult.HostShutdownToken;

            Assert.True(hostShutdownToken.WaitHandle.WaitOne(TimeoutExtensions.DefaultTimeout));
            Assert.True(hostShutdownToken.IsCancellationRequested);
        }

        private async Task<IISDeploymentResult> AssertStarts(HostingModel hostingModel)
        {
            var deploymentResult = await DeployApp(hostingModel);

            await AssertRunning(deploymentResult);

            return deploymentResult;
        }

        private static async Task AssertRunning(IISDeploymentResult deploymentResult)
        {
            var response = await deploymentResult.RetryingHttpClient.GetAsync("HelloWorld");

            var responseText = await response.Content.ReadAsStringAsync();
            Assert.Equal("Hello World", responseText);
        }

        private void AssertFilesNotLocked(IISDeploymentResult deploymentResult)
        {
            foreach (var file in Directory.GetFiles(deploymentResult.DeploymentResult.ContentRoot, "*", SearchOption.AllDirectories))
            {
                // Out of process module dll is allowed to be locked
                var name = Path.GetFileName(file);
                if (name == "aspnetcore.dll" || name == "aspnetcorev2.dll" || name == "aspnetcorev2_outofprocess.dll")
                {
                    continue;
                }
                File.Delete(file);
            }
        }

    }
}
