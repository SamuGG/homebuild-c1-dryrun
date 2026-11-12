using Microsoft.Playwright;

namespace HomeBuild.Web.Tests.EndToEnd;

/// <summary>A real floor-plan upload through the staff page: browser → S3 (CORS, pre-signed POST) → API check and move.</summary>
public sealed class FloorPlanUploadTests
{
    [Fact]
    public async Task Admin_uploads_a_floor_plan_from_the_browser_straight_to_s3()
    {
        Assert.SkipUnless(RealSignInTests.Enabled && Environment.GetEnvironmentVariable("HOMEBUILD_AWS_TESTS") == "1",
            "Set HOMEBUILD_COGNITO_TESTS=1 and HOMEBUILD_AWS_TESTS=1 (real pool and bucket).");
        var path = "/staff/units/0198a000-0000-7000-8001-000000000002/edit";
        await using var session = await RealSignInTests.SignedInPageAsync("admin.tenant-a@example.com", path);
        var page = session.Page;
        var s3Requests = new List<string>();
        page.RequestFinished += (_, r) => { if (r.Url.Contains("amazonaws.com", StringComparison.Ordinal)) { s3Requests.Add(r.Method); } };

        await page.Locator("#floor-plan").SetInputFilesAsync(new FilePayload
        {
            Name = "plan.pdf",
            MimeType = "application/pdf",
            Buffer = "%PDF-1.4\n1 0 obj <<>> endobj\ntrailer <<>>\n%%EOF\n"u8.ToArray(),
        });
        await page.GetByRole(AriaRole.Button, new() { Name = "Upload floor plan" }).ClickAsync();

        await Assertions.Expect(page.GetByTestId("floor-plan-status")).ToHaveTextAsync("Floor plan saved.", new() { Timeout = 20000 });
        Assert.Contains("POST", s3Requests); // the bytes went to S3, not through the web server
    }
}
