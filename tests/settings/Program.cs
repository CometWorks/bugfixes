using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Xml.Serialization;
using Shared.Config;

var serializer = new XmlSerializer(typeof(PluginConfig));
var properties = typeof(PluginConfig)
    .GetProperties()
    .Where(p => p.PropertyType == typeof(bool))
    .ToArray();
var defaults = new PluginConfig();

// Off by default: characters pushing resting grids changes gameplay, servers opt in.
string[] offByDefault = [nameof(PluginConfig.CharacterGridPushPatch)];
if (
    properties.Length != 42
    || properties.Any(p => (bool)p.GetValue(defaults) == offByDefault.Contains(p.Name))
    || typeof(PluginConfig).GetProperties().Length != 43
    || defaults.CharacterContactRestSpeed != 0f
)
    throw new Exception(
        "Expected one boolean per fix and the master switch, enabled unless off by default."
    );

using var oldXml = new StringReader(
    "<PluginConfig><Enabled>false</Enabled><WorldStorageInWorldFolder>false</WorldStorageInWorldFolder>"
        + "<IsolateMathStatics>false</IsolateMathStatics></PluginConfig>"
);
var migrated = (PluginConfig)serializer.Deserialize(oldXml);
if (
    migrated.Enabled
    || migrated.WorldStorageInWorldFolder
    || migrated.IsolateMathStatics
    || migrated.CharacterGridPushPatch
    || !migrated.CharacterContactPredictionPatch
    || migrated.CharacterContactRestSpeed != 0f
    || !migrated.HavokBatchRemovalPatch
)
    throw new Exception("Existing settings must survive, with new fixes at their defaults.");

var changes = 0;
string lastChanged = null;
defaults.PropertyChanged += (_, args) =>
{
    changes++;
    lastChanged = args.PropertyName;
};
defaults.CharacterContactRestSpeed = 2f;
foreach (var property in properties)
{
    var before = changes;
    var flipped = !(bool)property.GetValue(defaults);
    property.SetValue(defaults, flipped);
    property.SetValue(defaults, flipped);
    if (changes != before + 1 || lastChanged != property.Name)
        throw new Exception($"Incorrect change notification for {property.Name}.");
}

using var xml = new StringWriter();
serializer.Serialize(xml, defaults);
using var savedXml = new StringReader(xml.ToString());
var restored = (PluginConfig)serializer.Deserialize(savedXml);
if (
    properties.Any(p => (bool)p.GetValue(restored) != (bool)p.GetValue(defaults))
    || restored.CharacterContactRestSpeed != 2f
)
    throw new Exception("Changed values did not survive XML round-trip.");

Console.WriteLine(
    "PASS: 42 boolean defaults, rest speed, legacy config, change notifications, XML round-trip"
);
