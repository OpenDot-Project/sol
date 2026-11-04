// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;

namespace OpenDot.Sol.Lowering;

/// <summary>Validates documentation against compiler constants. Never consulted by code generation.</summary>
internal static class AbiManifest
{
    internal static XDocument Validate(Stream input)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = 1024 * 1024,
            MaxCharactersFromEntities = 1024,
            CloseInput = false,
        };
        using var reader = XmlReader.Create(input, settings);
        var document = XDocument.Load(reader, LoadOptions.None);
        var root = document.Root ?? throw new InvalidDataException("Missing ABI manifest root.");
        Require(root.Name == "sol-abi", "ABI manifest root");
        Attribute(root, "schema", "1"); Attribute(root, "family", "0"); Attribute(root, "revision", "2");
        Attribute(root, "contract", $"0x{RuntimeContract.Id:X16}");
        Attribute(root, "bridgeProtocol", $"0x{NativeAbi.BridgeProtocol:X16}");
        Attribute(root, "fir", "8"); Attribute(root, "solib", "3");
        var target = Single(root, "target");
        Attribute(target, "triple", "x86_64-pc-windows-msvc"); Attribute(target, "endianness", "little");
        Attribute(target, "pointerSize", "8"); Attribute(target, "callingConvention", "win64");
        Attribute(target, "allocationAlignment", NativeAbi.AllocationAlignment);
        var layouts = Single(root, "layouts").Elements().ToArray();
        Require(layouts.Length == NativeAbi.Layouts.Length, "ABI layout inventory");
        foreach (var expected in NativeAbi.Layouts)
        {
            var matches = layouts.Where(e => (string?)e.Attribute("name") == expected.Name).ToArray();
            Require(matches.Length == 1 && matches[0].Name == "layout", "ABI layout identity");
            var layout = matches[0];
            Attribute(layout, "size", expected.Size); Attribute(layout, "alignment", expected.Alignment);
            // LLVM type layout has natural alignment; explicit global/alloca alignment enforces alignas(16).
            Attribute(layout, "naturalAlignment", 8);
            var fields = layout.Elements().ToArray();
            Require(fields.Length == expected.Fields.Length, "ABI field inventory");
            int end = 0;
            for (int i = 0; i < fields.Length; i++)
            {
                var field = expected.Fields[i];
                Require(fields[i].Name == "field", "ABI field element");
                Attribute(fields[i], "name", field.Name); Attribute(fields[i], "offset", field.Offset);
                Attribute(fields[i], "size", field.Size);
                Attribute(fields[i], "gcReference", field.GcReference ? "true" : "false");
                int calculated = NativeAbi.Align(end, Math.Min(field.Size, 8));
                Require(calculated == field.Offset, "Compiler ABI offset calculation");
                end = checked(calculated + field.Size);
            }
            Require(NativeAbi.Align(end, expected.Alignment) == expected.Size, "Compiler ABI extent calculation");
        }
        var conventions = Single(root, "conventions").Elements().ToArray();
        Require(conventions.Length > 0, "Missing ABI conventions");
        Require(conventions.Select(e => (string?)e.Attribute("name")).Distinct(StringComparer.Ordinal).Count() == conventions.Length,
            "Duplicate ABI convention");
        foreach (var convention in conventions)
        {
            Require(convention.Name == "convention" && !string.IsNullOrWhiteSpace((string?)convention.Attribute("name"))
                && (string?)convention.Attribute("status") is "implemented" or "planned"
                && !string.IsNullOrWhiteSpace(convention.Value), "ABI convention status");
        }
        var exports = Single(root, "runtime-exports").Elements().ToArray();
        Require(exports.Length > 0 && exports.Select(e => (string?)e.Attribute("name"))
            .Distinct(StringComparer.Ordinal).Count() == exports.Length, "ABI export inventory");
        foreach (var export in exports)
        {
            Require(export.Name == "export" && (string?)export.Attribute("kind") is "function" or "data",
                "ABI export kind");
            Attribute(export, "status", "implemented");
            Require(!string.IsNullOrWhiteSpace((string?)export.Attribute("signature")), "ABI export signature");
            if ((string?)export.Attribute("kind") == "function")
            {
                foreach (string effect in new[] { "mayAllocate", "maySafepoint", "mayThrow" })
                { Require((string?)export.Attribute(effect) is "true" or "false", "ABI export effect"); }
            }
        }
        var envelope = Single(root, "orc-envelope");
        Attribute(envelope, "schema", "1"); Attribute(envelope, "magic", "SOLJIT01");
        Attribute(envelope, "headerSize", OrcEnvelope.HeaderSize);
        Attribute(envelope, "maximumPayloadSize", OrcEnvelope.MaximumPayloadSize);
        Attribute(envelope, "checksum", "SHA-256-payload"); Attribute(envelope, "loaderStatus", "implemented");
        Attribute(envelope, "jitProtocol", $"0x{NativeAbi.JitProtocol:X16}");
        (string Name, int Offset, int Size)[] envelopeFields =
        [
            ("Magic", 0, 8), ("Schema", 8, 4), ("HeaderSize", 12, 4), ("PayloadLength", 16, 8),
            ("RuntimeContract", 24, 8), ("FirSchema", 32, 4), ("LlvmMajor", 36, 4),
            ("LlvmMinor", 40, 4), ("LlvmPatch", 44, 4), ("Target", 48, 4), ("Flags", 52, 4),
            ("PayloadHash", 56, 32),
        ];
        var wireFields = envelope.Elements().ToArray();
        Require(wireFields.Length == envelopeFields.Length, "ORC envelope field inventory");
        for (int i = 0; i < wireFields.Length; i++)
        {
            Require(wireFields[i].Name == "field", "ORC envelope field element");
            Attribute(wireFields[i], "name", envelopeFields[i].Name);
            Attribute(wireFields[i], "offset", envelopeFields[i].Offset);
            Attribute(wireFields[i], "size", envelopeFields[i].Size);
        }
        Require(root.Elements().Count() == 5, "Unexpected ABI manifest section");
        return document;
    }

    private static XElement Single(XElement parent, string name)
    {
        var elements = parent.Elements(name).ToArray();
        Require(elements.Length == 1, "ABI section " + name);
        return elements[0];
    }

    private static void Attribute(XElement element, string name, int value)
        => Attribute(element, name, value.ToString(CultureInfo.InvariantCulture));

    private static void Attribute(XElement element, string name, string value)
        => Require((string?)element.Attribute(name) == value, "ABI attribute " + name);

    private static void Require(bool condition, string context)
    { if (!condition) { throw new InvalidDataException(context + " does not match the compiler contract."); } }
}
