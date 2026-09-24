namespace TarsClient.Tests;

[TestFixture]
public sealed class HotkeyServiceTests
{
    #region Fields

    private const int VkRightControl = 0xA3;

    #endregion

    #region Tests

    [TestCase("RightCtrl", VkRightControl)]
    [TestCase("rightctrl", VkRightControl)]
    [TestCase("F13", 0x7C)]
    [TestCase("Mouse4", VkRightControl)]
    [TestCase("", VkRightControl)]
    public void ParseKey_UnknownNamesFallBackToRightCtrl(string name, int expected)
    {
        #region Arrange
        // The key name comes from the test case.
        #endregion

        #region Act
        var vk = HotkeyService.ParseKey(name);
        #endregion

        #region Assert
        Assert.That(vk, Is.EqualTo(expected));
        #endregion
    }

    [TestCase("Ctrl+Alt+S", 0x53, 3)]
    [TestCase("control + shift + F12", 0x7B, 5)]
    [TestCase("Win+Pause", 0x13, 8)]
    public void ParseCombo_SplitsKeyAndModifiers(string combo, int expectedVk, int expectedMods)
    {
        #region Arrange
        // The combination comes from the test case.
        #endregion

        #region Act
        var (vk, mods) = HotkeyService.ParseCombo(combo);
        #endregion

        #region Assert
        Assert.That((vk, mods), Is.EqualTo((expectedVk, expectedMods)));
        #endregion
    }

    #endregion
}
