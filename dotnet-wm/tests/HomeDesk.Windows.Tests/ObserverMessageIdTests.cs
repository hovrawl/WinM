using System.Reflection;
using Xunit;

namespace HomeDesk.Windows.Tests;

public sealed class ObserverMessageIdTests
{
    [Fact]
    public void PrivateWindowMessageIdsAreUnique()
    {
        var messageIds = typeof(WindowsDesktopObserver)
            .GetFields(BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(field => field.Name.EndsWith("Message", StringComparison.Ordinal) && field.FieldType == typeof(uint))
            .Select(field => field.GetRawConstantValue())
            .ToArray();

        Assert.NotEmpty(messageIds);
        Assert.Equal(messageIds.Length, messageIds.Distinct().Count());
    }
}
