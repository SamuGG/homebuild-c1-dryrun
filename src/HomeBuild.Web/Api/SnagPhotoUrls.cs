namespace HomeBuild.Web.Api;

/// <summary>C10 Lab 5: the signed URLs for every photo on these snags, by S3 key (only snags that have photos are asked).</summary>
internal static class SnagPhotoUrls
{
    public static async Task<Dictionary<string, SnagPhoto>> LoadAsync(HomeApiClient api, IEnumerable<Snag> snags)
    {
        var withPhotos = snags.Where(s => s.Timeline.Any(e => e.Kind == "Photo")).ToList();
        var lists = await Task.WhenAll(withPhotos.Select(s => api.GetSnagPhotosAsync(s.Id)));
        return lists.SelectMany(l => l).ToDictionary(p => p.Key);
    }
}
