using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MatDock.Core.Notifications.WebPush;
using MatDock.Web.Support;
using Xunit;

namespace MatDock.Tests.Platform;

/// <summary>
/// Notifications on the devices of the users (Web Push, as in Matmon): the encryption against the example of RFC 8291, the VAPID signature with a key that
/// survives a restart, only public push addresses, and the device of the signed-in user switched on and off through the endpoints of pwa.js.
/// </summary>
public sealed class WebPushTests(MatDockFactory site) : IClassFixture<MatDockFactory>
{
    [Fact]
    public void The_encryption_matches_the_example_of_RFC_8291()
    {
        var asPrivate = WebPushEncryption.FromBase64Url("yfWPiYE-n46HLnH0KqZOF1fJJU3MYrct3AELtAQ-oRw")!;
        var asPublic = WebPushEncryption.FromBase64Url("BP4z9KsN6nGRTbVYI_c7VJSPQTBtkgcy27mlmlMoZIIgDll6e3vCYLocInmYWAmS6TlzAC8wEqKK6PBru3jl7A8")!;
        using var sender = ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            D = asPrivate,
            Q = new ECPoint { X = asPublic[1..33], Y = asPublic[33..65] },
        });

        var body = WebPushEncryption.Encrypt(
            "When I grow up, I want to be a watermelon"u8,
            WebPushEncryption.FromBase64Url("BCVxsr7N_eNgVRqvHtD0zTZsEc6-VV-JvLexhqUzORcxaOzi6-AYWXvTBHm4bjyPjs7Vd8pZGH6SRpkNtoIAiw4"),
            WebPushEncryption.FromBase64Url("BTBZMqHH6r4Tts7J_aSIgg"),
            sender,
            WebPushEncryption.FromBase64Url("DGv6ra1nlYgDCS1FRnbzlw"));

        Assert.Equal(
            "DGv6ra1nlYgDCS1FRnbzlwAAEABBBP4z9KsN6nGRTbVYI_c7VJSPQTBtkgcy27mlmlMoZIIgDll6e3vCYLocInmYWAmS6TlzAC8wEqKK6PBru3jl7A_yl95bQpu6cVPTpK4Mqgkf1CXztLVBSt2Ks3oZwbuwXPXLWyouBWLVWGNWQexSgSxsj_Qulcy4a-fN",
            WebPushEncryption.ToBase64Url(body));
    }

    [Fact]
    public void A_message_is_signed_with_the_key_of_the_installation_which_survives_a_restart_and_its_private_half_is_encrypted()
    {
        var dir = Directory.CreateTempSubdirectory("matdock-push").FullName;
        try
        {
            var path = Path.Combine(dir, "push-keys.json");
            var keys = new WebPushKeys(new FakeSecretProtector(), path);
            var header = keys.Authorization("https://fcm.googleapis.com/fcm/send/abc", "https://example.com", DateTimeOffset.UtcNow);
            var token = header["vapid t=".Length..header.IndexOf(',')];
            var parts = token.Split('.');
            Assert.True(keys.Verify(parts[0] + "." + parts[1], WebPushEncryption.FromBase64Url(parts[2])!));
            Assert.Contains("\"aud\":\"https://fcm.googleapis.com\"", Encoding.UTF8.GetString(WebPushEncryption.FromBase64Url(parts[1])!));
            Assert.Contains("enc:", File.ReadAllText(path)); // the private half through the secret protector

            Assert.Equal(keys.PublicKey, new WebPushKeys(new FakeSecretProtector(), path).PublicKey);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Theory]
    [InlineData("142.250.185.10", true)]
    [InlineData("2a00:1450:4001:82a::200a", true)]
    [InlineData("127.0.0.1", false)]
    [InlineData("10.1.2.3", false)]
    [InlineData("192.168.1.10", false)]
    [InlineData("172.20.0.5", false)]
    [InlineData("169.254.169.254", false)]
    [InlineData("::1", false)]
    [InlineData("fd00::1", false)]
    public void A_push_only_ever_goes_to_a_public_address(string address, bool expected)
        => Assert.Equal(expected, PublicNetwork.IsPublic(IPAddress.Parse(address)));

    [Fact]
    public void A_device_gets_a_name_from_its_browser()
    {
        Assert.Equal("Chrome on Android", WebPushService.DeviceName("Mozilla/5.0 (Linux; Android 14) AppleWebKit/537.36 Chrome/130.0 Mobile Safari/537.36"));
        Assert.Equal("Safari on iPhone", WebPushService.DeviceName("Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 Version/18.0 Mobile/15E148 Safari/604.1"));
    }

    private static object Subscription(string endpoint) => new
    {
        endpoint,
        keys = new { p256dh = "BCVxsr7N_eNgVRqvHtD0zTZsEc6-VV-JvLexhqUzORcxaOzi6-AYWXvTBHm4bjyPjs7Vd8pZGH6SRpkNtoIAiw4", auth = "BTBZMqHH6r4Tts7J_aSIgg" },
    };

    [Fact]
    public async Task The_signed_in_user_switches_a_device_on_and_off_and_another_site_cannot()
    {
        var browser = site.Browser();
        var login = await browser.GetStringAsync("/Account/Login");
        await SignIn.PostLocalFormAsync(browser, "/Account/Login", login);

        var key = await browser.GetFromJsonAsync<Dictionary<string, string>>("/ui/push/key");
        Assert.Equal(87, key!["publicKey"].Length); // 65 bytes, base64url

        // Without the header (a form of another site) nothing happens.
        Assert.Equal(HttpStatusCode.BadRequest, (await browser.PostAsJsonAsync("/ui/push/devices", Subscription("https://fcm.googleapis.com/fcm/send/one"))).StatusCode);

        var on = new HttpRequestMessage(HttpMethod.Post, "/ui/push/devices") { Content = JsonContent.Create(Subscription("https://fcm.googleapis.com/fcm/send/one")) };
        on.Headers.Add(PushEndpoints.Header, "1");
        Assert.Equal(HttpStatusCode.OK, (await browser.SendAsync(on)).StatusCode);
        Assert.Equal(1, site.Db(db => db.PushDevices.Count(d => d.Endpoint == "https://fcm.googleapis.com/fcm/send/one")));

        var bad = new HttpRequestMessage(HttpMethod.Post, "/ui/push/devices") { Content = JsonContent.Create(Subscription("http://10.0.0.1/push")) };
        bad.Headers.Add(PushEndpoints.Header, "1");
        Assert.Equal(HttpStatusCode.BadRequest, (await browser.SendAsync(bad)).StatusCode);

        var profile = await browser.GetStringAsync("/Account/Profile");
        Assert.Contains("data-push-device", profile);
        Assert.Contains("fcm.googleapis.com/fcm/send/one", profile);

        var off = new HttpRequestMessage(HttpMethod.Post, "/ui/push/devices/remove") { Content = JsonContent.Create(new { endpoint = "https://fcm.googleapis.com/fcm/send/one" }) };
        off.Headers.Add(PushEndpoints.Header, "1");
        var answer = await browser.SendAsync(off);
        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
        Assert.Contains("\"ok\":true", await answer.Content.ReadAsStringAsync());
        Assert.Equal(0, site.Db(db => db.PushDevices.Count(d => d.Endpoint == "https://fcm.googleapis.com/fcm/send/one")));

        // Switched on again: the same device, not a second one under the same address.
        var again = new HttpRequestMessage(HttpMethod.Post, "/ui/push/devices") { Content = JsonContent.Create(Subscription("https://fcm.googleapis.com/fcm/send/one")) };
        again.Headers.Add(PushEndpoints.Header, "1");
        Assert.Equal(HttpStatusCode.OK, (await browser.SendAsync(again)).StatusCode);
        Assert.Equal(1, site.Db(db => db.PushDevices.Count(d => d.Endpoint == "https://fcm.googleapis.com/fcm/send/one")));

        // Not signed in: the sign-in page, not the key.
        var anonymous = await site.Browser().GetAsync("/ui/push/key");
        Assert.NotEqual(HttpStatusCode.OK, anonymous.StatusCode);
    }

    [Fact]
    public async Task A_notification_reaches_the_device_encrypted_for_it_and_a_device_that_is_gone_is_forgotten()
    {
        using var database = new TestDatabase();
        using var phone = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var phoneKey = phone.ExportParameters(false);
        var phonePublic = new byte[] { 0x04 }.Concat(phoneKey.Q.X!).Concat(phoneKey.Q.Y!).ToArray();
        var auth = RandomNumberGenerator.GetBytes(16);
        database.Context.PushDevices.Add(new MatDock.Core.Entities.PushDevice
        {
            UserId = 1, Endpoint = "https://fcm.googleapis.com/fcm/send/phone", Name = "Chrome on Android",
            P256dh = WebPushEncryption.ToBase64Url(phonePublic), Auth = WebPushEncryption.ToBase64Url(auth),
        });
        await database.Context.SaveChangesAsync();

        var dir = Directory.CreateTempSubdirectory("matdock-push").FullName;
        try
        {
            var http = new PushServiceFake();
            var push = new WebPushService(database.Context, new WebPushKeys(new FakeSecretProtector(), Path.Combine(dir, "push-keys.json")), http,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<WebPushService>.Instance);

            Assert.Empty(await push.SendAllAsync("Backup failed", "mdtest-vol → Local", "/Backups"));
            var (headers, body) = Assert.Single(http.Requests);
            Assert.StartsWith("vapid t=", headers["Authorization"]);
            Assert.Equal("aes128gcm", headers["Content-Encoding"]);

            using var message = JsonDocument.Parse(Decrypt(body, phone, phonePublic, auth));
            Assert.Equal("Backup failed", message.RootElement.GetProperty("title").GetString());
            Assert.Equal("mdtest-vol → Local", message.RootElement.GetProperty("body").GetString());
            Assert.Equal("/Backups", message.RootElement.GetProperty("url").GetString());

            http.Status = HttpStatusCode.Gone; // the user switched notifications off in the browser
            Assert.Empty(await push.SendAllAsync("Again", "-", "/"));
            Assert.Equal(0, database.NewContext().PushDevices.Count());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>What the browser does with a message (RFC 8291, the receiving side): proves it is readable by the device and only by it.</summary>
    private static string Decrypt(byte[] body, ECDiffieHellman receiver, byte[] receiverPublic, byte[] auth)
    {
        var salt = body[..16];
        var idLength = body[20];
        var senderPublic = body[21..(21 + idLength)];
        var cipher = body[(21 + idLength)..];

        using var sender = ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = senderPublic[1..33], Y = senderPublic[33..65] },
        });
        var shared = receiver.DeriveRawSecretAgreement(sender.PublicKey);
        var keyInfo = "WebPush: info\0"u8.ToArray().Concat(receiverPublic).Concat(senderPublic).ToArray();
        var ikm = HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, 32, auth, keyInfo);
        var prk = HKDF.Extract(HashAlgorithmName.SHA256, ikm, salt);
        var cek = HKDF.Expand(HashAlgorithmName.SHA256, prk, 16, "Content-Encoding: aes128gcm\0"u8.ToArray());
        var nonce = HKDF.Expand(HashAlgorithmName.SHA256, prk, 12, "Content-Encoding: nonce\0"u8.ToArray());

        var plain = new byte[cipher.Length - 16];
        using (var aes = new AesGcm(cek, 16))
        {
            aes.Decrypt(nonce, cipher[..^16], cipher[^16..], plain);
        }

        var end = Array.LastIndexOf(plain, (byte)0x02); // the delimiter of the last record, padding after it
        return Encoding.UTF8.GetString(plain, 0, end);
    }

    private sealed class PushServiceFake : IHttpClientFactory
    {
        public HttpStatusCode Status { get; set; } = HttpStatusCode.Created;
        public List<(Dictionary<string, string> Headers, byte[] Body)> Requests { get; } = [];

        public HttpClient CreateClient(string name) => new(new Handler(this));

        private sealed class Handler(PushServiceFake owner) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var headers = request.Headers.Concat(request.Content!.Headers).ToDictionary(h => h.Key, h => string.Join(",", h.Value));
                owner.Requests.Add((headers, await request.Content.ReadAsByteArrayAsync(cancellationToken)));
                return new HttpResponseMessage(owner.Status);
            }
        }
    }
}
