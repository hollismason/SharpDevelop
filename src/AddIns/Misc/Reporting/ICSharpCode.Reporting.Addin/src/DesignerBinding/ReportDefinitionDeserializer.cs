﻿// Copyright (c) 2014 AlphaSierraPapa for the SharpDevelop Team
// 
// Permission is hereby granted, free of charge, to any person obtaining a copy of this
// software and associated documentation files (the "Software"), to deal in the Software
// without restriction, including without limitation the rights to use, copy, modify, merge,
// publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons
// to whom the Software is furnished to do so, subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be included in all copies or
// substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
// INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR
// PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE
// FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
// OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
// DEALINGS IN THE SOFTWARE.

using System;
using System.ComponentModel.Design;
using System.IO;
using System.Reflection;
using System.Xml;

using ICSharpCode.Core;
using ICSharpCode.Reporting.Factories;
using ICSharpCode.Reporting.Items;
using ICSharpCode.Reporting.Xml;
using ICSharpCode.Reporting.Addin.XML;

namespace ICSharpCode.Reporting.Addin.DesignerBinding
{
	class ReportDefinitionDeserializer : ReportDefinitionParser
	{
		IDesignerHost host;
		
		public ReportDefinitionDeserializer(IDesignerHost host)
		{
			Console.WriteLine("ReportDefinitionDeserializer");
			if (host == null) {
				throw new ArgumentNullException("host");
			}
		
			this.host = host;
		}
		
		public XmlDocument LoadXmlFromStream(Stream stream)
		{
			Console.Write("LoadXml");
			if (stream == null)
				throw new ArgumentNullException("stream");
			var xmlDocument = new XmlDocument();
			xmlDocument.Load(stream);
			if (xmlDocument.FirstChild.NodeType == XmlNodeType.XmlDeclaration) {
				var xmlDeclaration = (XmlDeclaration)xmlDocument.FirstChild;
				xmlDeclaration.Encoding = "utf-8";
			}
			return xmlDocument;
		}
		
		
		public ReportModel CreateModelFromXml(XmlElement elem)
		{
			Console.WriteLine("CreateModelFromXml");
			
			var reportSettings = CreateReportSettings(elem);
		
			var reportModel = ReportModelFactory.Create();
			reportModel.ReportSettings = reportSettings;
			
			host.Container.Add(reportSettings);
			
			//Move to SectionCollection
			XmlNodeList sectionList =  elem.LastChild.ChildNodes;
			
			foreach (XmlNode sectionNode in sectionList) {
				try {
					object o = this.Load(sectionNode as XmlElement,null);
					var section = o as ICSharpCode.Reporting.Addin.DesignableItems.BaseSection;
					host.Container.Add(section);
				} catch (Exception e) {
					MessageService.ShowException(e);
				}
			}
			return reportModel;
		}

		
		ReportSettings CreateReportSettings(XmlElement elem)
		{
			XmlNodeList nodes = elem.FirstChild.ChildNodes;
			var reportSettingsNode = (XmlElement)nodes[0];
			var modelLoader = new ModelLoader();
			return  modelLoader.Load(reportSettingsNode) as ReportSettings;
		}
		
		
		protected override Type GetTypeByName(string ns, string name)
		{
			var a = Assembly.GetExecutingAssembly();
			Type t = a.GetType("ICSharpCode.Reporting.Addin.DesignableItems" + "." + name);
//			Type t = typeof(BaseSection).Assembly.GetType(typeof(BaseSection).Namespace + "." + name);
				
			return t;
		}
	}
}
