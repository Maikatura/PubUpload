namespace FileUploader.Services;

public static class ShareXConfigBuilder
{
    public static object Build(string baseUrl) => new
    {
        Version = "12.4.1",
        Name = "Gock Uploader",
        DestinationType = "ImageUploader, FileUploader",
        RequestMethod = "POST",
        RequestURL = $"{baseUrl}/api/upload",
        Body = "MultipartFormData",
        FileFormName = "file",
        URL = "$json:url$",
        DeletionURL = "$json:deletionURL$"
    };
}
