using System.Net;
using System.Text.RegularExpressions;
using FamilyTree.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

internal static class FictionalWorkbenchHttpAssertions
{
    internal static async Task RunAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Development"
        });
        builder.WebHost.UseTestServer();
        builder.Services.AddAntiforgery();
        builder.Services.AddSingleton<SyntheticReviewWorkbench>();
        await using var app = builder.Build();
        // In-process TestServer has no socket RemoteIpAddress. Simulate the
        // loopback TCP peer, with an explicit non-loopback negative scenario.
        app.Use(async (context, next) =>
        {
            context.Connection.RemoteIpAddress =
                context.Request.Headers["X-Test-Remote"] == "external"
                ? IPAddress.Parse("203.0.113.10") : IPAddress.Loopback;
            if (context.Request.Headers["X-Test-Chunked"] == "yes")
                context.Request.ContentLength = null;
            await next();
        });
        SyntheticWorkbenchRoutes.Map(app);
        app.MapGet("/demo", (Delegate)(Func<HttpContext, Task<IResult>>)SyntheticTreeExplorer.ShowAsync);
        await app.StartAsync();
        using var client = app.GetTestClient();
        client.BaseAddress = new Uri("http://localhost");

        using (var explorer = await client.GetAsync("/demo?above=8&below=0&social=yes&spouses=no&siblings=no"))
        {
            var body=await explorer.Content.ReadAsStringAsync();
            Check(explorer.StatusCode == HttpStatusCode.OK &&
                  body.Contains("Fictional family explorer",StringComparison.Ordinal) &&
                  body.Contains("Center on ",StringComparison.Ordinal) &&
                  body.Contains("Try local fictional editing and review",StringComparison.Ordinal),
                "fictional explorer recenter and option toggles rendered");
        }
        using (var invalid = await client.GetAsync("/demo?center=invalid&above=99&below=-3&social=no"))
        {
            var body=await invalid.Content.ReadAsStringAsync();
            Check(invalid.StatusCode == HttpStatusCode.OK &&
                  body.Contains("Visible fictional people",StringComparison.Ordinal),
                "invalid center and out-of-range generation options recover safely");
        }

        using (var remote = new HttpRequestMessage(HttpMethod.Get, "/demo/workbench"))
        {
            remote.Headers.Add("X-Test-Remote", "external");
            using var denied = await client.SendAsync(remote);
            Check(denied.StatusCode == HttpStatusCode.NotFound,
                "non-loopback fictional workbench denied");
        }
        using (var hosted = new HttpRequestMessage(HttpMethod.Get, "/demo/workbench"))
        {
            hosted.Headers.Host = "familytree.public.example";
            using var denied = await client.SendAsync(hosted);
            Check(denied.StatusCode == HttpStatusCode.NotFound,
                "localhost proxy cannot expose the workbench under a public Host");
        }
        using (var ipv6 = new HttpRequestMessage(HttpMethod.Get, "/demo/workbench"))
        {
            ipv6.Headers.Host = "[::1]:5182";
            using var allowed = await client.SendAsync(ipv6);
            Check(allowed.StatusCode == HttpStatusCode.OK,
                "IPv6 loopback Host accepted even with square brackets");
        }
        using var first = await client.GetAsync("/demo/workbench");
        Check(first.StatusCode == HttpStatusCode.OK, "local workbench GET works");
        var html = await first.Content.ReadAsStringAsync();
        Check(html.Contains("Invented records only.", StringComparison.Ordinal) &&
            html.Contains("Fictional Founder", StringComparison.Ordinal) &&
            !html.Contains("PRIVATE", StringComparison.Ordinal),
            "rendered view contains only invented persons");

        var antiforgery = Regex.Match(html,
            "name='__RequestVerificationToken' value='([^']+)'");
        Check(antiforgery.Success, "anti-CSRF request token rendered");
        var token = antiforgery.Groups[1].Value;
        var cookie = first.Headers.TryGetValues("Set-Cookie", out var cookies)
            ? cookies.First().Split(';')[0]
            : throw new InvalidOperationException("Anti-CSRF cookie missing");

        async Task<HttpResponseMessage> Send(string action,
            bool includeToken = true, bool includeCookie = true,
            bool external = false, string? versionOverride = null)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post,
                "/demo/workbench");
            using var currentPage = await client.GetAsync("/demo/workbench");
            var currentHtml = await currentPage.Content.ReadAsStringAsync();
            var versionMatch = Regex.Match(currentHtml, "name='version' value='([0-9]+)'");
            Check(versionMatch.Success, "view revision token rendered");
            var version = versionOverride ?? versionMatch.Groups[1].Value;
            request.Content = new FormUrlEncodedContent(
                includeToken
                ? [
                    new KeyValuePair<string,string>("action",action),
                    new KeyValuePair<string,string>("version",version),
                    new KeyValuePair<string,string>("__RequestVerificationToken",token)
                  ]
                : [new KeyValuePair<string,string>("action",action), new KeyValuePair<string,string>("version",version)]);
            if (includeCookie) request.Headers.Add("Cookie", cookie);
            if (external) request.Headers.Add("X-Test-Remote", "external");
            return await client.SendAsync(request);
        }

        using (var oversize = await Send(new string('x', 3000)))
            Check(oversize.StatusCode == HttpStatusCode.BadRequest,
                "oversized form rejected before parsing");
        using (var multipart = new HttpRequestMessage(HttpMethod.Post, "/demo/workbench"))
        {
            multipart.Headers.Add("Cookie", cookie);
            var content = new MultipartFormDataContent();
            content.Add(new StringContent("propose-person"), "action");
            content.Add(new StringContent(token), "__RequestVerificationToken");
            content.Add(new ByteArrayContent([1,2,3]), "private-file", "secret.png");
            multipart.Content = content;
            using var denied = await client.SendAsync(multipart);
            Check(denied.StatusCode == HttpStatusCode.BadRequest,
                "multipart file upload prohibited");
        }
        using (var chunked = new HttpRequestMessage(HttpMethod.Post, "/demo/workbench"))
        {
            chunked.Headers.Add("Cookie",cookie);
            chunked.Headers.Add("X-Test-Chunked","yes");
            chunked.Content = new FormUrlEncodedContent(
            [
                new("action", "propose-person"),
                new("__RequestVerificationToken", token)
            ]);
            using var denied = await client.SendAsync(chunked);
            Check(denied.StatusCode == HttpStatusCode.BadRequest,
                "unknown-length chunked body prohibited");
        }
        using (var denied = await Send("propose-person", includeToken: false))
            Check(denied.StatusCode == HttpStatusCode.BadRequest,
                "CSRF request without token rejected");
        using (var denied = await Send("propose-person", includeCookie: false))
            Check(denied.StatusCode == HttpStatusCode.BadRequest,
                "CSRF request without cookie rejected");
        using (var denied = await Send("propose-person", external: true))
            Check(denied.StatusCode == HttpStatusCode.NotFound,
                "remote POST cannot modify fictional data");
        using (var denied = await Send("invalid"))
            Check(denied.StatusCode == HttpStatusCode.Conflict,
                "unknown action rejected");
        using (var denied = await Send("approve"))
            Check(denied.StatusCode == HttpStatusCode.Conflict,
                "premature moderator approval rejected");

        using (var response = await Send("propose-person"))
            Check((int)response.StatusCode == 303, "fictional proposal accepted");
        using (var stale = await Send("reject", versionOverride: "0"))
            Check(stale.StatusCode == HttpStatusCode.Conflict,
                "stale tab cannot reject a newer pending proposal");
        using (var response = await Send("propose-person"))
            Check(response.StatusCode == HttpStatusCode.Conflict,
                "second pending proposal rejected");
        using (var pending = await client.GetAsync("/demo/workbench"))
        {
            var pendingBody = await pending.Content.ReadAsStringAsync();
            Check(pendingBody.Contains("AddPerson", StringComparison.Ordinal) &&
                !pendingBody.Contains("Fictional Guest 1", StringComparison.Ordinal),
                "pending person not disclosed as approved");
        }

        using (var response = await Send("approve"))
            Check((int)response.StatusCode == 303, "moderator approves guest");
        using (var approved = await client.GetAsync("/demo/workbench"))
            Check((await approved.Content.ReadAsStringAsync())
                .Contains("Fictional Guest 1", StringComparison.Ordinal),
                "approved fictional person visible");

        using (var response = await Send("propose-link"))
            Check((int)response.StatusCode == 303, "link proposal submitted");
        using (var response = await Send("approve"))
            Check((int)response.StatusCode == 303, "link approved");
        using (var linked = await client.GetAsync("/demo/workbench"))
            Check((await linked.Content.ReadAsStringAsync())
                .Contains("1 links have passed", StringComparison.Ordinal),
                "authorized fictional link visible");

        using (var undone = await Send("undo"))
            Check((int)undone.StatusCode == 303, "reviewed change undone");
        using (var restored = await client.GetAsync("/demo/workbench"))
            Check((await restored.Content.ReadAsStringAsync())
                .Contains("0 links have passed", StringComparison.Ordinal),
                "undo removed relationship");
        using (var p = await Send("propose-person"))
            Check((int)p.StatusCode == 303, "second fictional proposal");
        using (var reject = await Send("reject"))
            Check((int)reject.StatusCode == 303, "reject is a review decision");
        using (var bad = new HttpRequestMessage(HttpMethod.Post, "/demo/workbench"))
        {
            bad.Headers.Add("Cookie",cookie);
            bad.Content = new FormUrlEncodedContent(
            [
                new("action","approve"),
                new("__RequestVerificationToken",token),
                new("another","invalid")
            ]);
            using var invalid = await client.SendAsync(bad);
            Check(invalid.StatusCode == HttpStatusCode.BadRequest,
                "unexpected form fields rejected");
        }
        // Exhaust the bounded audit without adding approved links. The form
        // must disable propose-link rather than offering an action that 409s.
        while (app.Services.GetRequiredService<SyntheticReviewWorkbench>()
            .Read().Audit.Count < 80)
        {
            using var propose = await Send("propose-link");
            Check((int)propose.StatusCode == 303, "link proposal before audit cap");
            using var reject = await Send("reject");
            Check((int)reject.StatusCode == 303, "rejection before audit cap");
        }
        using (var exhausted = await client.GetAsync("/demo/workbench"))
        {
            var exhaustedHtml = await exhausted.Content.ReadAsStringAsync();
            Check(Regex.IsMatch(exhaustedHtml,
                "name='action' value='propose-link' disabled"),
                "link button disabled at audit cap");
        }
        using (var denied = await Send("propose-link"))
            Check(denied.StatusCode == HttpStatusCode.Conflict,
                "server rejects action at audit cap even when submitted manually");
        await app.StopAsync();
        Console.WriteLine("PASS in-process fictional HTTP editor CSRF, loopback, review and undo");
    }
    private static void Check(bool ok,string description)
    {
        if (!ok) throw new InvalidOperationException(description);
    }
}
