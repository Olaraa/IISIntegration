// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Testing.xunit;
using Microsoft.Extensions.Logging.Testing;
using Xunit;
using Xunit.Abstractions;

namespace Microsoft.AspNetCore.Server.IISIntegration.FunctionalTests
{
    [SkipIfHostableWebCoreNotAvailable]
    [OSSkipCondition(OperatingSystems.Windows, WindowsVersions.Win7, "https://github.com/aspnet/IISIntegration/issues/866")]
    public class TestServerTest: LoggedTest
    {
        public TestServerTest(ITestOutputHelper output = null) : base(output)
        {
        }

        [ConditionalFact]
        public async Task SingleProcessTestServer_HelloWorld()
        {
            var helloWorld = "Hello World";
            var expectedPath = "/Path";

            string path = null;
            using (var testServer = await TestServer.Create(ctx => {
                path = ctx.Request.Path.ToString();
                return ctx.Response.WriteAsync(helloWorld);
            }, LoggerFactory))
            {
                var result = await testServer.HttpClient.GetAsync(expectedPath);
                Assert.Equal(helloWorld, await result.Content.ReadAsStringAsync());
                Assert.Equal(expectedPath, path);
            }
        }

        [ConditionalFact]
        [OSSkipCondition(OperatingSystems.Windows, WindowsVersions.Win7, "https://github.com/aspnet/IISIntegration/issues/866")]
        public async Task WritesSucceedAfterClientDisconnect()
        {
            TaskCompletionSource<bool> requestStartedCompletionSource = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<bool> requestCompletedCompletionSource = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            var data = new byte[1024];
            using (var testServer = await TestServer.Create(async ctx => {
                requestStartedCompletionSource.SetResult(true);

                for (int i = 0; i < 1000; i++)
                {
                    await ctx.Response.Body.WriteAsync(data);
                }

                requestCompletedCompletionSource.SetResult(true);
            }, LoggerFactory))
            {
                using (var connection = testServer.CreateConnection())
                {
                    await connection.Send(
                        "POST / HTTP/1.1",
                        $"Content-Length: {100000}",
                        "Host: localhost",
                        "Connection: close",
                        "",
                        "");

                    await requestStartedCompletionSource.Task.TimeoutAfterDefault();
                }

                await requestCompletedCompletionSource.Task.TimeoutAfterDefault();
            }
        }

        [ConditionalFact]
        [OSSkipCondition(OperatingSystems.Windows, WindowsVersions.Win7, "https://github.com/aspnet/IISIntegration/issues/866")]
        public async Task ReadThrowsAfterClientDisconnect()
        {
            TaskCompletionSource<bool> requestStartedCompletionSource = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<bool> requestCompletedCompletionSource = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            Exception exception = null;

            var data = new byte[1024];
            using (var testServer = await TestServer.Create(async ctx => {
                requestStartedCompletionSource.SetResult(true);
                try
                {
                    await ctx.Request.Body.ReadAsync(data);
                }
                catch (Exception e)
                {
                    exception = e;
                }

                requestCompletedCompletionSource.SetResult(true);
            }, LoggerFactory))
            {
                using (var connection = testServer.CreateConnection())
                {
                    await connection.Send(
                        "POST / HTTP/1.1",
                        $"Content-Length: {100000}",
                        "Host: localhost",
                        "Connection: close",
                        "",
                        "");

                    await requestStartedCompletionSource.Task.TimeoutAfterDefault();
                }

                await requestCompletedCompletionSource.Task.TimeoutAfterDefault();
            }

            Assert.IsType<IOException>(exception);
            Assert.Equal("Native IO operation failed", exception.Message);
        }
    }
}
