using System.Reflection;
using PicoVibe.App.Services;
using PicoVibe.Core.Configuration;

namespace PicoVibe.Runtime.Tests;

public sealed class AppConfigCloneTests
{
    /// <summary>
    /// The orchestrator clones the configuration property by property, so a new
    /// setting that nobody adds to the clone is silently ignored at runtime
    /// while the file on disk still shows it as enabled.
    /// </summary>
    [Fact]
    public void CloneKeepsEveryGlobalSetting()
    {
        var config = AppConfig.CreateDefault();
        PropertyInfo[] properties = typeof(GlobalSettings)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.CanRead && property.CanWrite)
            .ToArray();
        Assert.NotEmpty(properties);
        foreach (var property in properties)
        {
            property.SetValue(config.Global, DistinctValue(property, config.Global));
        }

        var clone = LightOrchestrator.CloneConfig(config);

        foreach (var property in properties)
        {
            Assert.Equal(
                property.GetValue(config.Global),
                property.GetValue(clone.Global));
        }
    }

    private static object DistinctValue(PropertyInfo property, GlobalSettings source)
    {
        var current = property.GetValue(source);
        if (property.PropertyType == typeof(bool))
        {
            return !(bool)current!;
        }
        if (property.PropertyType == typeof(int))
        {
            return (int)current! + 7;
        }
        if (property.PropertyType.IsEnum)
        {
            var values = Enum.GetValues(property.PropertyType);
            foreach (var value in values)
            {
                if (!Equals(value, current))
                {
                    return value;
                }
            }
            return current!;
        }
        throw new NotSupportedException(
            $"GlobalSettings.{property.Name} has an unhandled type; extend this test.");
    }
}
