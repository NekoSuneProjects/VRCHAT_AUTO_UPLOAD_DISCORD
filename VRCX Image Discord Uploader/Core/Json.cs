using System.Text.Encodings.Web;
using System.Text.Json;

namespace VRCX_Image_Discord_Uploader.Core;

static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // keep world names readable in the files
    };

    /// <summary>Compact output for the bot payload (same shape as the old Newtonsoft client).</summary>
    public static readonly JsonSerializerOptions Compact = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
