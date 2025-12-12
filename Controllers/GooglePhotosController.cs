// Controllers/GooglePhotosController.cs
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace Google_Photos_Integration_API.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class GooglePhotosController : ControllerBase
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<GooglePhotosController> _logger;

        public GooglePhotosController(IHttpClientFactory httpClientFactory, ILogger<GooglePhotosController> logger)
        {
            _httpClientFactory = httpClientFactory;
            _logger = logger;
        }

        // Single file upload. Optional header: X-Google-AlbumId
        [HttpPost("upload")]
        public async Task<IActionResult> UploadToGooglePhotos(
           IFormFile file,
           [FromHeader(Name = "X-Google-AccessToken")] string accessToken,
           [FromHeader(Name = "X-Google-AlbumId")] string? albumId)
        {
            if (file == null || file.Length == 0)
                return BadRequest(new { error = "No file uploaded." });

            if (string.IsNullOrWhiteSpace(accessToken))
                return BadRequest(new { error = "Missing Google access token." });

            byte[] fileBytes;
            using (var ms = new MemoryStream())
            {
                await file.CopyToAsync(ms);
                fileBytes = ms.ToArray();
            }

            string uploadToken;
            try
            {
                var client = _httpClientFactory.CreateClient();
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

                client.DefaultRequestHeaders.Remove("X-Goog-Upload-File-Name");
                client.DefaultRequestHeaders.Add("X-Goog-Upload-File-Name", file.FileName);
                client.DefaultRequestHeaders.Remove("X-Goog-Upload-Protocol");
                client.DefaultRequestHeaders.Add("X-Goog-Upload-Protocol", "raw");

                using var content = new ByteArrayContent(fileBytes);
                content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

                var uploadResponse = await client.PostAsync("https://photoslibrary.googleapis.com/v1/uploads", content);
                var uploadText = await uploadResponse.Content.ReadAsStringAsync();

                if (!uploadResponse.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Upload (raw) failed: {Status} {Body}", (int)uploadResponse.StatusCode, uploadText);
                    return StatusCode((int)uploadResponse.StatusCode, new { error = uploadText });
                }

                uploadToken = uploadText.Trim();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error uploading raw bytes to Google Photos");
                return StatusCode(500, new { error = ex.Message });
            }

            try
            {
                var client = _httpClientFactory.CreateClient();
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

                var newItem = new { simpleMediaItem = new { uploadToken = uploadToken } };

                var body = new
                {
                    albumId = string.IsNullOrWhiteSpace(albumId) ? null : albumId,
                    newMediaItems = new[] { newItem }
                };

                var json = JsonConvert.SerializeObject(body, new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });

                using var content = new StringContent(json, Encoding.UTF8, "application/json");
                var createResponse = await client.PostAsync("https://photoslibrary.googleapis.com/v1/mediaItems:batchCreate", content);
                var createText = await createResponse.Content.ReadAsStringAsync();

                if (!createResponse.IsSuccessStatusCode)
                {
                    _logger.LogWarning("mediaItems:batchCreate failed: {Status} {Body}", (int)createResponse.StatusCode, createText);
                    return StatusCode((int)createResponse.StatusCode, new { error = createText });
                }

                var parsed = JsonConvert.DeserializeObject<object>(createText)!;
                return Ok(parsed);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating media item");
                return StatusCode(500, new { error = ex.Message });
            }
        }

        // Multiple files endpoint: uploads each file and batch-creates them
        [HttpPost("upload/multi")]
        public async Task<IActionResult> UploadMultiple(
            List<IFormFile> files,
            [FromHeader(Name = "X-Google-AccessToken")] string accessToken,
            [FromHeader(Name = "X-Google-AlbumId")] string? albumId)
        {
            if (files == null || files.Count == 0)
                return BadRequest(new { error = "No files uploaded." });

            if (string.IsNullOrWhiteSpace(accessToken))
                return BadRequest(new { error = "Missing Google access token." });

            var uploadTokens = new List<string>();

            foreach (var file in files)
            {
                try
                {
                    byte[] fileBytes;
                    using (var ms = new MemoryStream())
                    {
                        await file.CopyToAsync(ms);
                        fileBytes = ms.ToArray();
                    }

                    var client = _httpClientFactory.CreateClient();
                    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

                    client.DefaultRequestHeaders.Remove("X-Goog-Upload-File-Name");
                    client.DefaultRequestHeaders.Add("X-Goog-Upload-File-Name", file.FileName);
                    client.DefaultRequestHeaders.Remove("X-Goog-Upload-Protocol");
                    client.DefaultRequestHeaders.Add("X-Goog-Upload-Protocol", "raw");

                    using var content = new ByteArrayContent(fileBytes);
                    content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

                    var uploadResponse = await client.PostAsync("https://photoslibrary.googleapis.com/v1/uploads", content);
                    var uploadText = await uploadResponse.Content.ReadAsStringAsync();

                    if (!uploadResponse.IsSuccessStatusCode)
                    {
                        _logger.LogWarning("Upload (raw) failed for file {Name}: {Status} {Body}", file.FileName, (int)uploadResponse.StatusCode, uploadText);
                        return StatusCode((int)uploadResponse.StatusCode, new { error = uploadText });
                    }

                    uploadTokens.Add(uploadText.Trim());
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error uploading file {Name}", file.FileName);
                    return StatusCode(500, new { error = ex.Message });
                }
            }

            var newMediaItems = uploadTokens.Select(t => new { simpleMediaItem = new { uploadToken = t } }).ToArray();
            var bodyObj = new
            {
                albumId = string.IsNullOrWhiteSpace(albumId) ? null : albumId,
                newMediaItems = newMediaItems
            };

            try
            {
                var json = JsonConvert.SerializeObject(bodyObj, new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });
                using var content = new StringContent(json, Encoding.UTF8, "application/json");

                var client2 = _httpClientFactory.CreateClient();
                client2.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

                var createResponse = await client2.PostAsync("https://photoslibrary.googleapis.com/v1/mediaItems:batchCreate", content);
                var createText = await createResponse.Content.ReadAsStringAsync();

                if (!createResponse.IsSuccessStatusCode)
                {
                    _logger.LogWarning("mediaItems:batchCreate failed: {Status} {Body}", (int)createResponse.StatusCode, createText);
                    return StatusCode((int)createResponse.StatusCode, new { error = createText });
                }

                var parsed = JsonConvert.DeserializeObject<object>(createText)!;
                return Ok(parsed);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in mediaItems:batchCreate");
                return StatusCode(500, new { error = ex.Message });
            }
        }

        // GET media items — optional albumId (search), supports pagination tokens
        [HttpGet("photos")]
        public async Task<IActionResult> GetPhotos(
            [FromHeader(Name = "X-Google-AccessToken")] string accessToken,
            [FromQuery] string? albumId = null,
            [FromQuery] int pageSize = 50,
            [FromQuery] string? pageToken = null)
        {
            if (string.IsNullOrWhiteSpace(accessToken))
                return BadRequest(new { error = "Missing Google access token." });

            try
            {
                var client = _httpClientFactory.CreateClient();
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

                HttpResponseMessage response;
                if (!string.IsNullOrWhiteSpace(albumId))
                {
                    var body = new { albumId, pageSize, pageToken = string.IsNullOrWhiteSpace(pageToken) ? null : pageToken };
                    var json = JsonConvert.SerializeObject(body);
                    using var content = new StringContent(json, Encoding.UTF8, "application/json");
                    response = await client.PostAsync("https://photoslibrary.googleapis.com/v1/mediaItems:search", content);
                }
                else
                {
                    var url = $"https://photoslibrary.googleapis.com/v1/mediaItems?pageSize={pageSize}";
                    if (!string.IsNullOrWhiteSpace(pageToken)) url += $"&pageToken={Uri.EscapeDataString(pageToken)}";
                    response = await client.GetAsync(url);
                }

                var text = await response.Content.ReadAsStringAsync();
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Photos API failed: {Status} {Body}", (int)response.StatusCode, text);
                    return StatusCode((int)response.StatusCode, new { error = text });
                }

                var parsed = JsonConvert.DeserializeObject<object>(text)!;
                return Ok(parsed);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error while calling Google Photos API (GetPhotos)");
                return StatusCode(500, new { error = ex.Message });
            }
        }

        // GET albums (simple list)
        [HttpGet("albums")]
        public async Task<IActionResult> ListAlbums([FromHeader(Name = "X-Google-AccessToken")] string accessToken, [FromQuery] int pageSize = 50)
        {
            if (string.IsNullOrWhiteSpace(accessToken))
                return BadRequest(new { error = "Missing Google access token." });

            try
            {
                var client = _httpClientFactory.CreateClient();
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

                var url = $"https://photoslibrary.googleapis.com/v1/albums?pageSize={pageSize}";
                var resp = await client.GetAsync(url);
                var text = await resp.Content.ReadAsStringAsync();

                if (!resp.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Albums list failed: {Status} {Body}", (int)resp.StatusCode, text);
                    return StatusCode((int)resp.StatusCode, new { error = text });
                }

                var parsed = JsonConvert.DeserializeObject<object>(text)!;
                return Ok(parsed);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error listing albums");
                return StatusCode(500, new { error = ex.Message });
            }
        }

        // POST create album
        [HttpPost("albums")]
        public async Task<IActionResult> CreateAlbum([FromHeader(Name = "X-Google-AccessToken")] string accessToken, [FromBody] CreateAlbumRequest req)
        {
            if (string.IsNullOrWhiteSpace(accessToken))
                return BadRequest(new { error = "Missing Google access token." });

            if (req == null || string.IsNullOrWhiteSpace(req.Title))
                return BadRequest(new { error = "Album title required." });

            try
            {
                var client = _httpClientFactory.CreateClient();
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

                var body = new { album = new { title = req.Title } };
                var json = JsonConvert.SerializeObject(body, new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });
                using var content = new StringContent(json, Encoding.UTF8, "application/json");

                var resp = await client.PostAsync("https://photoslibrary.googleapis.com/v1/albums", content);
                var text = await resp.Content.ReadAsStringAsync();

                if (!resp.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Create album failed: {Status} {Body}", (int)resp.StatusCode, text);
                    return StatusCode((int)resp.StatusCode, new { error = text });
                }

                var parsed = JsonConvert.DeserializeObject<object>(text)!;
                return Ok(parsed);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating album");
                return StatusCode(500, new { error = ex.Message });
            }
        }

        public class CreateAlbumRequest
        {
            public string Title { get; set; } = string.Empty;
        }
    }
}
