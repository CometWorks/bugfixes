using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Xml.Serialization;
using Shared.Config;

var serializer = new XmlSerializer(typeof(PluginConfig));
var properties = typeof(PluginConfig).GetProperties();
var defaults = new PluginConfig();
if (
    properties.Length != 34
    || properties.Any(p => p.PropertyType != typeof(bool) || !(bool)p.GetValue(defaults))
)
    throw new Exception("Expected one enabled boolean per fix and the master switch.");

using var oldXml = new StringReader(
    "<PluginConfig><Enabled>false</Enabled><WorldStorageInWorldFolder>false</WorldStorageInWorldFolder>"
        + "<IsolateMathStatics>false</IsolateMathStatics></PluginConfig>"
);
var migrated = (PluginConfig)serializer.Deserialize(oldXml);
if (
    migrated.Enabled
    || migrated.WorldStorageInWorldFolder
    || migrated.IsolateMathStatics
    || !migrated.HavokBatchRemovalPatch
)
    throw new Exception("Existing settings must survive, with new fixes enabled.");

var changes = 0;
string lastChanged = null;
defaults.PropertyChanged += (_, args) =>
{
    changes++;
    lastChanged = args.PropertyName;
};
foreach (var property in properties)
{
    var before = changes;
    property.SetValue(defaults, false);
    property.SetValue(defaults, false);
    if (changes != before + 1 || lastChanged != property.Name)
        throw new Exception($"Incorrect change notification for {property.Name}.");
}

using var xml = new StringWriter();
serializer.Serialize(xml, defaults);
using var savedXml = new StringReader(xml.ToString());
var restored = (PluginConfig)serializer.Deserialize(savedXml);
if (properties.Any(p => (bool)p.GetValue(restored)))
    throw new Exception("Disabled values did not survive XML round-trip.");

Console.WriteLine("PASS: 34 boolean defaults, legacy config, change notifications, XML round-trip");
