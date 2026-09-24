namespace TarsClient.Tests;

[TestFixture]
public sealed class AdminApiTests
{
    #region Tests

    [TestCase("wss://192.168.86.243:8765/ws", "https://192.168.86.243:8765")]
    [TestCase("ws://tars.lan:8765/ws?x=1", "http://tars.lan:8765")]
    [TestCase("WSS://tars.lan/ws", "https://tars.lan")]
    public void HttpBase_MapsWebSocketUrlToHttpOrigin(string wsUrl, string expected)
    {
        #region Arrange
        // The URL comes from the test case.
        #endregion

        #region Act
        var httpBase = AdminApi.HttpBase(wsUrl);
        #endregion

        #region Assert
        Assert.That(httpBase, Is.EqualTo(expected));
        #endregion
    }

    [Test]
    public void ExtractAccessKey_TakesTheKeyNotTheHeaderName()
    {
        #region Arrange
        const string spec = "# Spec\n\n- **Access key** (`x-tars-key` header): `k3y-Abc_123`\n- **CA**: below\n";
        #endregion

        #region Act
        var key = AdminApi.ExtractAccessKey(spec);
        #endregion

        #region Assert
        Assert.That(key, Is.EqualTo("k3y-Abc_123"));
        #endregion
    }

    [Test]
    public void ExtractAccessKey_RedactedSpec_ReturnsNull()
    {
        #region Arrange
        const string spec = "- **Access key** (`x-tars-key` header): `<redacted: the server fills it in when the spec is downloaded; the client fetches it itself>`\n";
        #endregion

        #region Act
        var key = AdminApi.ExtractAccessKey(spec);
        #endregion

        #region Assert
        Assert.That(key, Is.Null);
        #endregion
    }

    #endregion
}
