﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace System.Resources
{

    using System.Diagnostics;
    using System.Reflection;
    using System;
    using System.Windows.Forms;
    using Microsoft.Win32;
    using System.Drawing;
    using System.IO;
    using System.Text;
    using System.ComponentModel;
    using System.Collections;
    using System.Resources;
    using System.Xml;
    using System.Runtime.Serialization;
    using System.Diagnostics.CodeAnalysis;

    /// <summary>
    ///     ResX resource writer. See the text in "ResourceSchema" for more 
    ///     information.
    /// </summary>
    public class ResXResourceWriter : IResourceWriter
    {
        internal const string TypeStr = "type";
        internal const string NameStr = "name";
        internal const string DataStr = "data";
        internal const string MetadataStr = "metadata";
        internal const string MimeTypeStr = "mimetype";
        internal const string ValueStr = "value";
        internal const string ResHeaderStr = "resheader";
        internal const string VersionStr = "version";
        internal const string ResMimeTypeStr = "resmimetype";
        internal const string ReaderStr = "reader";
        internal const string WriterStr = "writer";
        internal const string CommentStr = "comment";
        internal const string AssemblyStr = "assembly";
        internal const string AliasStr = "alias";

        private Hashtable cachedAliases;

        private static readonly TraceSwitch ResValueProviderSwitch = new TraceSwitch("ResX", "Debug the resource value provider");

        public static readonly string BinSerializedObjectMimeType = "application/x-microsoft.net.object.binary.base64";
        public static readonly string SoapSerializedObjectMimeType = "application/x-microsoft.net.object.soap.base64";
        public static readonly string DefaultSerializedObjectMimeType = BinSerializedObjectMimeType;
        public static readonly string ByteArraySerializedObjectMimeType = "application/x-microsoft.net.object.bytearray.base64";
        public static readonly string ResMimeType = "text/microsoft-resx";
        public static readonly string Version = "2.0";

        public static readonly string ResourceSchema = @"
    <xsd:schema id=""root"" xmlns="""" xmlns:xsd=""http://www.w3.org/2001/XMLSchema"" xmlns:msdata=""urn:schemas-microsoft-com:xml-msdata"">
        <xsd:import namespace=""http://www.w3.org/XML/1998/namespace""/>
        <xsd:element name=""root"" msdata:IsDataSet=""true"">
            <xsd:complexType>
                <xsd:choice maxOccurs=""unbounded"">
                    <xsd:element name=""metadata"">
                        <xsd:complexType>
                            <xsd:sequence>
                            <xsd:element name=""value"" type=""xsd:string"" minOccurs=""0""/>
                            </xsd:sequence>
                            <xsd:attribute name=""name"" use=""required"" type=""xsd:string""/>
                            <xsd:attribute name=""type"" type=""xsd:string""/>
                            <xsd:attribute name=""mimetype"" type=""xsd:string""/>
                            <xsd:attribute ref=""xml:space""/>                            
                        </xsd:complexType>
                    </xsd:element>
                    <xsd:element name=""assembly"">
                      <xsd:complexType>
                        <xsd:attribute name=""alias"" type=""xsd:string""/>
                        <xsd:attribute name=""name"" type=""xsd:string""/>
                      </xsd:complexType>
                    </xsd:element>
                    <xsd:element name=""data"">
                        <xsd:complexType>
                            <xsd:sequence>
                                <xsd:element name=""value"" type=""xsd:string"" minOccurs=""0"" msdata:Ordinal=""1"" />
                                <xsd:element name=""comment"" type=""xsd:string"" minOccurs=""0"" msdata:Ordinal=""2"" />
                            </xsd:sequence>
                            <xsd:attribute name=""name"" type=""xsd:string"" use=""required"" msdata:Ordinal=""1"" />
                            <xsd:attribute name=""type"" type=""xsd:string"" msdata:Ordinal=""3"" />
                            <xsd:attribute name=""mimetype"" type=""xsd:string"" msdata:Ordinal=""4"" />
                            <xsd:attribute ref=""xml:space""/>
                        </xsd:complexType>
                    </xsd:element>
                    <xsd:element name=""resheader"">
                        <xsd:complexType>
                            <xsd:sequence>
                                <xsd:element name=""value"" type=""xsd:string"" minOccurs=""0"" msdata:Ordinal=""1"" />
                            </xsd:sequence>
                            <xsd:attribute name=""name"" type=""xsd:string"" use=""required"" />
                        </xsd:complexType>
                    </xsd:element>
                </xsd:choice>
            </xsd:complexType>
        </xsd:element>
        </xsd:schema>
        ";
        readonly string fileName;
        Stream stream;
        TextWriter textWriter;
        XmlTextWriter xmlTextWriter;

        bool hasBeenSaved;
        bool initialized;

        private readonly Func<Type, string> typeNameConverter; // no public property to be consistent with ResXDataNode class.

        /// <summary>
        ///     Base Path for ResXFileRefs.
        /// </summary>
        public string BasePath { get; set; }

        /// <summary>
        ///     Creates a new ResXResourceWriter that will write to the specified file.
        /// </summary>
        public ResXResourceWriter(string fileName)
        {
            this.fileName = fileName;
        }
        public ResXResourceWriter(string fileName, Func<Type, string> typeNameConverter)
        {
            this.fileName = fileName;
            this.typeNameConverter = typeNameConverter;
        }

        /// <summary>
        ///     Creates a new ResXResourceWriter that will write to the specified stream.
        /// </summary>
        public ResXResourceWriter(Stream stream)
        {
            this.stream = stream;
        }
        public ResXResourceWriter(Stream stream, Func<Type, string> typeNameConverter)
        {
            this.stream = stream;
            this.typeNameConverter = typeNameConverter;
        }

        /// <summary>
        ///     Creates a new ResXResourceWriter that will write to the specified TextWriter.
        /// </summary>
        public ResXResourceWriter(TextWriter textWriter)
        {
            this.textWriter = textWriter;
        }
        public ResXResourceWriter(TextWriter textWriter, Func<Type, string> typeNameConverter)
        {
            this.textWriter = textWriter;
            this.typeNameConverter = typeNameConverter;
        }

        ~ResXResourceWriter()
        {
            Dispose(false);
        }

        private void InitializeWriter()
        {
            if (xmlTextWriter == null)
            {
                // 

                bool writeHeaderRequired = false;

                if (textWriter != null)
                {
                    textWriter.WriteLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
                    writeHeaderRequired = true;

                    xmlTextWriter = new XmlTextWriter(textWriter);
                }
                else if (stream != null)
                {
                    xmlTextWriter = new XmlTextWriter(stream, System.Text.Encoding.UTF8);
                }
                else
                {
                    Debug.Assert(fileName != null, "Nothing to output to");
                    xmlTextWriter = new XmlTextWriter(fileName, System.Text.Encoding.UTF8);
                }
                xmlTextWriter.Formatting = Formatting.Indented;
                xmlTextWriter.Indentation = 2;

                if (!writeHeaderRequired)
                {
                    xmlTextWriter.WriteStartDocument(); // writes <?xml version="1.0" encoding="utf-8"?>
                }
            }
            else
            {
                xmlTextWriter.WriteStartDocument();
            }

            xmlTextWriter.WriteStartElement("root");
            XmlTextReader reader = new XmlTextReader(new StringReader(ResourceSchema))
            {
                WhitespaceHandling = WhitespaceHandling.None
            };
            xmlTextWriter.WriteNode(reader, true);

            xmlTextWriter.WriteStartElement(ResHeaderStr);
            {
                xmlTextWriter.WriteAttributeString(NameStr, ResMimeTypeStr);
                xmlTextWriter.WriteStartElement(ValueStr);
                {
                    xmlTextWriter.WriteString(ResMimeType);
                }
                xmlTextWriter.WriteEndElement();
            }
            xmlTextWriter.WriteEndElement();
            xmlTextWriter.WriteStartElement(ResHeaderStr);
            {
                xmlTextWriter.WriteAttributeString(NameStr, VersionStr);
                xmlTextWriter.WriteStartElement(ValueStr);
                {
                    xmlTextWriter.WriteString(Version);
                }
                xmlTextWriter.WriteEndElement();
            }
            xmlTextWriter.WriteEndElement();
            xmlTextWriter.WriteStartElement(ResHeaderStr);
            {
                xmlTextWriter.WriteAttributeString(NameStr, ReaderStr);
                xmlTextWriter.WriteStartElement(ValueStr);
                {
                    xmlTextWriter.WriteString(MultitargetUtil.GetAssemblyQualifiedName(typeof(ResXResourceReader), typeNameConverter));
                }
                xmlTextWriter.WriteEndElement();
            }
            xmlTextWriter.WriteEndElement();
            xmlTextWriter.WriteStartElement(ResHeaderStr);
            {
                xmlTextWriter.WriteAttributeString(NameStr, WriterStr);
                xmlTextWriter.WriteStartElement(ValueStr);
                {
                    xmlTextWriter.WriteString(MultitargetUtil.GetAssemblyQualifiedName(typeof(ResXResourceWriter), typeNameConverter));
                }
                xmlTextWriter.WriteEndElement();
            }
            xmlTextWriter.WriteEndElement();

            initialized = true;
        }

        private XmlWriter Writer
        {
            get
            {
                if (!initialized)
                {
                    InitializeWriter();
                }
                return xmlTextWriter;
            }
        }

        /// <summary>
        ///    Adds aliases to the resource file...
        /// </summary>
        public virtual void AddAlias(string aliasName, AssemblyName assemblyName)
        {
            if (assemblyName == null)
            {
                throw new ArgumentNullException(nameof(assemblyName));
            }

            if (cachedAliases == null)
            {
                cachedAliases = new Hashtable();
            }

            cachedAliases[assemblyName.FullName] = aliasName;
        }


        /// <summary>
        ///    Adds the given value to the collection of metadata.  These name/value pairs 
        ///    will be emitted to the <metadata> elements in the .resx file.
        /// </summary>
        public void AddMetadata(string name, byte[] value)
        {
            AddDataRow(MetadataStr, name, value);
        }

        /// <summary>
        ///    Adds the given value to the collection of metadata.  These name/value pairs 
        ///    will be emitted to the <metadata> elements in the .resx file.
        /// </summary>
        public void AddMetadata(string name, string value)
        {
            AddDataRow(MetadataStr, name, value);
        }

        /// <summary>
        ///    Adds the given value to the collection of metadata.  These name/value pairs 
        ///    will be emitted to the <metadata> elements in the .resx file.
        /// </summary>
        public void AddMetadata(string name, object value)
        {
            AddDataRow(MetadataStr, name, value);
        }

        /// <summary>
        ///     Adds a blob resource to the resources.
        /// </summary>
        // NOTE: Part of IResourceWriter - not protected by class level LinkDemand.
        public void AddResource(string name, byte[] value)
        {
            AddDataRow(DataStr, name, value);
        }

        /// <summary>
        ///     Adds a resource to the resources. If the resource is a string,
        ///     it will be saved that way, otherwise it will be serialized
        ///     and stored as in binary.
        /// </summary>
        // NOTE: Part of IResourceWriter - not protected by class level LinkDemand.
        public void AddResource(string name, object value)
        {
            if (value is ResXDataNode node)
            {
                AddResource(node);
            }
            else
            {
                AddDataRow(DataStr, name, value);
            }
        }

        /// <summary>
        ///     Adds a string resource to the resources.
        /// </summary>
        // NOTE: Part of IResourceWriter - not protected by class level LinkDemand.
        public void AddResource(string name, string value)
        {
            AddDataRow(DataStr, name, value);
        }

        /// <summary>
        ///     Adds a string resource to the resources.
        /// </summary>
        public void AddResource(ResXDataNode node)
        {
            // we're modifying the node as we're adding it to the resxwriter
            // this is BAD, so we clone it. adding it to a writer doesnt change it
            // we're messing with a copy
            ResXDataNode nodeClone = node.DeepClone();

            ResXFileRef fileRef = nodeClone.FileRef;
            string modifiedBasePath = BasePath;

            if (!string.IsNullOrEmpty(modifiedBasePath))
            {
                if (!modifiedBasePath.EndsWith("\\"))
                {
                    modifiedBasePath += "\\";
                }

                fileRef?.MakeFilePathRelative(modifiedBasePath);
            }
            DataNodeInfo info = nodeClone.GetDataNodeInfo();
            AddDataRow(DataStr, info.Name, info.ValueData, info.TypeName, info.MimeType, info.Comment);
        }

        /// <summary>
        ///     Adds a blob resource to the resources.
        /// </summary>
        private void AddDataRow(string elementName, string name, byte[] value)
        {
            AddDataRow(elementName, name, ToBase64WrappedString(value), TypeNameWithAssembly(typeof(byte[])), null, null);
        }

        /// <summary>
        ///     Adds a resource to the resources. If the resource is a string,
        ///     it will be saved that way, otherwise it will be serialized
        ///     and stored as in binary.
        /// </summary>
        private void AddDataRow(string elementName, string name, object value)
        {
            Debug.WriteLineIf(ResValueProviderSwitch.TraceVerbose, "  resx: adding resource " + name);
            switch (value)
            {
                case string str:
                    AddDataRow(elementName, name, str);
                    break;
                case byte[] bytes:
                    AddDataRow(elementName, name, bytes);
                    break;
                case ResXFileRef fileRef:
                    {
                        ResXDataNode node = new ResXDataNode(name, fileRef, typeNameConverter);
                        DataNodeInfo info = node.GetDataNodeInfo();
                        AddDataRow(elementName, info.Name, info.ValueData, info.TypeName, info.MimeType, info.Comment);
                        break;
                    }
                default:
                    {
                        ResXDataNode node = new ResXDataNode(name, value, typeNameConverter);
                        DataNodeInfo info = node.GetDataNodeInfo();
                        AddDataRow(elementName, info.Name, info.ValueData, info.TypeName, info.MimeType, info.Comment);
                        break;
                    }
            }
        }

        /// <summary>
        ///     Adds a string resource to the resources.
        /// </summary>
        private void AddDataRow(string elementName, string name, string value)
        {
            // if it's a null string, set it here as a resxnullref
            string typeName =
                value == null
                    ? MultitargetUtil.GetAssemblyQualifiedName(typeof(ResXNullRef), typeNameConverter)
                    : null;
            AddDataRow(elementName, name, value, typeName, null, null);
        }

        /// <summary>
        ///     Adds a new row to the Resources table. This helper is used because
        ///     we want to always late bind to the columns for greater flexibility.
        /// </summary>
        [SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes")]
        private void AddDataRow(string elementName, string name, string value, string type, string mimeType, string comment)
        {
            if (hasBeenSaved)
            {
                throw new InvalidOperationException(SR.ResXResourceWriterSaved);
            }

            string alias = null;
            if (!string.IsNullOrEmpty(type) && elementName == DataStr)
            {
                string assemblyName = GetFullName(type);
                if (string.IsNullOrEmpty(assemblyName))
                {
                    try
                    {
                        Type typeObject = Type.GetType(type);
                        if (typeObject == typeof(string))
                        {
                            type = null;
                        }
                        else if (typeObject != null)
                        {
                            assemblyName = GetFullName(MultitargetUtil.GetAssemblyQualifiedName(typeObject, typeNameConverter));
                            alias = GetAliasFromName(new AssemblyName(assemblyName));
                        }
                    }
                    catch
                    {
                    }
                }
                else
                {
                    alias = GetAliasFromName(new AssemblyName(GetFullName(type)));
                }
                //AddAssemblyRow(AssemblyStr, alias, GetFullName(type));
            }

            Writer.WriteStartElement(elementName);
            {
                Writer.WriteAttributeString(NameStr, name);

                if (!string.IsNullOrEmpty(alias) && !string.IsNullOrEmpty(type) && elementName == DataStr)
                {
                    // CHANGE: we still output version information. This might have
                    // to change in 3.2
                    string typeName = GetTypeName(type);
                    string typeValue = typeName + ", " + alias;
                    Writer.WriteAttributeString(TypeStr, typeValue);
                }
                else
                {
                    if (type != null)
                    {
                        Writer.WriteAttributeString(TypeStr, type);
                    }
                }

                if (mimeType != null)
                {
                    Writer.WriteAttributeString(MimeTypeStr, mimeType);
                }

                if ((type == null && mimeType == null) || (type != null && type.StartsWith("System.Char", StringComparison.Ordinal)))
                {
                    Writer.WriteAttributeString("xml", "space", null, "preserve");
                }

                Writer.WriteStartElement(ValueStr);
                {
                    if (!string.IsNullOrEmpty(value))
                    {
                        Writer.WriteString(value);
                    }
                }
                Writer.WriteEndElement();
                if (!string.IsNullOrEmpty(comment))
                {
                    Writer.WriteStartElement(CommentStr);
                    {
                        Writer.WriteString(comment);
                    }
                    Writer.WriteEndElement();
                }
            }
            Writer.WriteEndElement();
        }


        private void AddAssemblyRow(string elementName, string alias, string name)
        {
            Writer.WriteStartElement(elementName);
            {
                if (!string.IsNullOrEmpty(alias))
                {
                    Writer.WriteAttributeString(AliasStr, alias);
                }

                if (!string.IsNullOrEmpty(name))
                {
                    Writer.WriteAttributeString(NameStr, name);
                }
                //Writer.WriteEndElement();
            }
            Writer.WriteEndElement();
        }

        private string GetAliasFromName(AssemblyName assemblyName)
        {
            if (cachedAliases == null)
            {
                cachedAliases = new Hashtable();
            }
            string alias = (string)cachedAliases[assemblyName.FullName];
            if (string.IsNullOrEmpty(alias))
            {
                alias = assemblyName.Name;
                AddAlias(alias, assemblyName);
                AddAssemblyRow(AssemblyStr, alias, assemblyName.FullName);
            }
            return alias;
        }

        /// <summary>
        ///     Closes any files or streams locked by the writer.
        /// </summary>
        // NOTE: Part of IResourceWriter - not protected by class level LinkDemand.
        public void Close()
        {
            Dispose();
        }

        // NOTE: Part of IDisposable - not protected by class level LinkDemand.
        public virtual void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (!hasBeenSaved)
                {
                    Generate();
                }
                if (xmlTextWriter != null)
                {
                    xmlTextWriter.Close();
                    xmlTextWriter = null;
                }
                if (stream != null)
                {
                    stream.Close();
                    stream = null;
                }
                if (textWriter != null)
                {
                    textWriter.Close();
                    textWriter = null;
                }
            }
        }

        private string GetTypeName(string typeName)
        {
            int indexStart = typeName.IndexOf(',');
            return ((indexStart == -1) ? typeName : typeName.Substring(0, indexStart));
        }

        private string GetFullName(string typeName)
        {
            int indexStart = typeName.IndexOf(',');
            if (indexStart == -1)
            {
                return null;
            }

            return typeName.Substring(indexStart + 2);
        }

        static string ToBase64WrappedString(byte[] data)
        {
            const int lineWrap = 80;
            const string crlf = "\r\n";
            const string prefix = "        ";
            string raw = Convert.ToBase64String(data);
            if (raw.Length > lineWrap)
            {
                StringBuilder output = new StringBuilder(raw.Length + (raw.Length / lineWrap) * 3); // word wrap on lineWrap chars, \r\n
                int current = 0;
                for (; current < raw.Length - lineWrap; current += lineWrap)
                {
                    output.Append(crlf);
                    output.Append(prefix);
                    output.Append(raw, current, lineWrap);
                }
                output.Append(crlf);
                output.Append(prefix);
                output.Append(raw, current, raw.Length - current);
                output.Append(crlf);
                return output.ToString();
            }

            return raw;
        }

        private string TypeNameWithAssembly(Type type)
        {
            string result = MultitargetUtil.GetAssemblyQualifiedName(type, typeNameConverter);
            return result;
        }

        /// <summary>
        ///     Writes the resources out to the file or stream.
        /// </summary>
        // NOTE: Part of IResourceWriter - not protected by class level LinkDemand.
        public void Generate()
        {
            if (hasBeenSaved)
            {
                throw new InvalidOperationException(SR.ResXResourceWriterSaved);
            }

            hasBeenSaved = true;
            Debug.WriteLineIf(ResValueProviderSwitch.TraceVerbose, "writing XML");

            Writer.WriteEndElement();
            Writer.Flush();

            Debug.WriteLineIf(ResValueProviderSwitch.TraceVerbose, "done");
        }
    }
}



