using System.IO.Compression;
using Amazon;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.AspNetCore.Http.Features;

var builder = WebApplication.CreateSlimBuilder(args);

builder.Services.AddSingleton<IAmazonS3>(sp =>
{
    var accessKey = Environment.GetEnvironmentVariable("DEEPDEEPBIM_REVIT_ADDIN_UPDATE_S3_ACCESS_KEY");

    if (string.IsNullOrEmpty(accessKey))
    {
        throw new Exception($"NOT FOUND: {nameof(accessKey)}.");
    }

    var secretKey = Environment.GetEnvironmentVariable("DEEPDEEPBIM_REVIT_ADDIN_UPDATE_S3_SECRET_KEY");

    if (string.IsNullOrEmpty(secretKey))
    {
        throw new Exception($"NOT FOUND: {nameof(secretKey)}.");
    }

    return new AmazonS3Client(accessKey, secretKey, RegionEndpoint.USEast1);
});

var app = builder.Build();

app.MapPost("deepdeepbim/api/update-revit-addin", async (HttpContext context, IAmazonS3 s3Client) =>
{
    string? receivedUserKey = context.Request.Headers["X-DeepDeepBim-Key"];

    if (string.IsNullOrEmpty(receivedUserKey))
    {
        return Results.InternalServerError($"MISSING KEY: {nameof(receivedUserKey)}.");
    }

    var userKeyIsValid = ValidateUserKey(receivedUserKey);

    if (!userKeyIsValid)
    {
        return Results.Unauthorized();
    }

    var bucketName = Environment.GetEnvironmentVariable("DEEPDEEPBIM_REVIT_ADDIN_UPDATE_S3_BUCKET_NAME");

    if (string.IsNullOrEmpty(bucketName))
    {
        return Results.InternalServerError($"NOT FOUND: {nameof(bucketName)}.");
    }

    ListObjectsV2Request listRequest = new ListObjectsV2Request
    {
        BucketName = bucketName
    };

    ListObjectsV2Response listResponse = await s3Client.ListObjectsV2Async(listRequest);

    List<S3Object> filesToDownload = listResponse.S3Objects.Where(a =>
            a.Key.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                ||
            a.Key.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
        )
        .ToList();

    if (!filesToDownload.Any())
    {
        return Results.NotFound("No updates found.");
    }

    long? totalUncompressedBytes = filesToDownload.Sum(f => f.Size);

    context.Response.Headers.Append("X-File-Count", filesToDownload.Count.ToString());
    context.Response.Headers.Append("X-Total-Uncompressed-Size", totalUncompressedBytes.ToString());

    return Results.Stream(async (outputStream) =>
    {
        var syncIOFeature = context.Features.Get<IHttpBodyControlFeature>();
        if (syncIOFeature != null)
        {
            syncIOFeature.AllowSynchronousIO = true;
        }

        using var archive = new System.IO.Compression.ZipArchive(outputStream, ZipArchiveMode.Create, leaveOpen: false);

        foreach (var item in filesToDownload)
        {
            var fileName = Path.GetFileName(item.Key);
            if (string.IsNullOrEmpty(fileName)) continue;

            var zipEntry = archive.CreateEntry(fileName, CompressionLevel.Fastest);
            using var zipEntryStream = zipEntry.Open();

            await StreamS3FileToZipAsync(s3Client, bucketName, item.Key, zipEntryStream);
        }
    },
    contentType: "application/zip",
    fileDownloadName: "RevitAddinUpdate.zip");
    });

app.Run();

bool ValidateUserKey(string userKey)
{
    var validKey = Environment.GetEnvironmentVariable("DEEPDEEPBIM_REVIT_ADDIN_UPDATE_VALID_KEY");

    if (string.IsNullOrEmpty(userKey))
    {
        throw new Exception($"NOT FOUND: {nameof(userKey)}.");
    }

    return userKey.Equals(validKey);
}

async Task StreamS3FileToZipAsync(IAmazonS3 client, string bucketName, string key, Stream destination)
{
    try
    {
        var getRequest = new GetObjectRequest
        {
            BucketName = bucketName,
            Key = key
        };
        using var response = await client.GetObjectAsync(getRequest);
        using var responseStream = response.ResponseStream;
        await responseStream.CopyToAsync(destination);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Failed to download {key}: {ex.Message}");
    }
}