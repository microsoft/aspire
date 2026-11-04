// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Xml;
using System.Xml.Linq;

namespace Aspire.TestTools;

// Shared by the canonical test-report reader and CI analysis. CI analysis consumes the
// result's raw display name; definitions are retrieval metadata, not failure identity.
internal static class TrxDocument
{
    public static XDocument Load(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = XmlReader.Create(
            stream,
            new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
            });
        var document = XDocument.Load(reader);
        if (document.Root is not { Name.LocalName: "TestRun" } root ||
            root.Name.NamespaceName is not ("" or "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"))
        {
            throw new XmlException("Test result does not have a TRX TestRun root.");
        }

        return document;
    }
}
