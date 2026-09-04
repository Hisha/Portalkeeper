using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace Portalkeeper.Services;

public static class UserErrorService
{
    public static string Format(Exception exception, string? context = null)
    {
        var message = exception switch
        {
            TaskCanceledException =>
                "The operation timed out. Check your network connection and try again.",

            HttpRequestException http when http.StatusCode is not null =>
                $"The remote server returned HTTP {(int)http.StatusCode.Value} ({http.StatusCode.Value}). Try again later.",

            HttpRequestException =>
                "Portalkeeper could not reach the remote server. Check your network connection and try again.",

            UnauthorizedAccessException =>
                "Portalkeeper does not have permission to access the required file or folder.",

            JsonException =>
                "The configuration data is not valid JSON.",

            FileNotFoundException file when !string.IsNullOrWhiteSpace(file.FileName) =>
                $"A required file was not found: {file.FileName}",

            DirectoryNotFoundException =>
                "A required folder could not be found.",

            IOException io when !string.IsNullOrWhiteSpace(io.Message) =>
                $"A file operation failed: {io.Message}",

            InvalidDataException invalid when !string.IsNullOrWhiteSpace(invalid.Message) =>
                invalid.Message,

            ArgumentException argument when !string.IsNullOrWhiteSpace(argument.Message) =>
                argument.Message,

            InvalidOperationException invalidOperation when !string.IsNullOrWhiteSpace(invalidOperation.Message) =>
                invalidOperation.Message,

            _ when !string.IsNullOrWhiteSpace(exception.Message) =>
                exception.Message,

            _ =>
                "An unexpected error occurred."
        };

        return string.IsNullOrWhiteSpace(context)
            ? message
            : $"{context}: {message}";
    }
}
