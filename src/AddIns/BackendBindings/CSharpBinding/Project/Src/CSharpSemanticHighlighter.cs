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
using System.Collections.Generic;
using System.ComponentModel.Design;
using System.Diagnostics;
using System.Linq;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.Core;
using ICSharpCode.NRefactory;
using ICSharpCode.NRefactory.CSharp;
using ICSharpCode.NRefactory.CSharp.Analysis;
using ICSharpCode.NRefactory.CSharp.Resolver;
using ICSharpCode.NRefactory.Editor;
using ICSharpCode.NRefactory.Semantics;
using ICSharpCode.NRefactory.TypeSystem;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Parser;
using CSharpBinding.Parser;

namespace CSharpBinding
{
	/// <summary>
	/// Semantic highlighting for C#.
	/// </summary>
	public class CSharpSemanticHighlighter : SemanticHighlightingVisitor<HighlightingColor>, IHighlighter
	{
		readonly IDocument document;
		
		List<IDocumentLine> invalidLines;
		List<CachedLine> cachedLines;
		bool hasCrashed;
		bool forceParseOnNextRefresh;
		bool eventHandlersAreRegistered;
		bool inHighlightingGroup;
		
		int lineNumber;
		HighlightedLine line;
		CSharpFullParseInformation parseInfo;
		ISymbolReference currentSymbolReference;
		IResolveVisitorNavigator currentNavigator;
		HighlightingColor symbolReferenceColor;
		
		#region Constructor + Dispose
		public CSharpSemanticHighlighter(IDocument document)
		{
			if (document == null)
				throw new ArgumentNullException("document");
			this.document = document;
			
			var highlighting = HighlightingManager.Instance.GetDefinition("C#");
			//this.defaultTextColor = ???;
			this.referenceTypeColor = highlighting.GetNamedColor("ReferenceTypes");
			this.valueTypeColor = highlighting.GetNamedColor("ValueTypes");
			this.interfaceTypeColor = this.referenceTypeColor;
			this.enumerationTypeColor = this.valueKeywordColor;
			this.typeParameterTypeColor = this.referenceTypeColor;
			this.delegateTypeColor = this.referenceTypeColor;
			this.symbolReferenceColor = new HighlightingColor { Background = new SimpleHighlightingBrush(DefaultFillColor) };
			
			this.methodDeclarationColor = this.methodCallColor = highlighting.GetNamedColor("MethodCall");
			//this.eventDeclarationColor = this.eventAccessColor = defaultTextColor;
			//this.propertyDeclarationColor = this.propertyAccessColor = defaultTextColor;
			this.fieldDeclarationColor = this.fieldAccessColor = highlighting.GetNamedColor("FieldAccess");
			//this.variableDeclarationColor = this.variableAccessColor = defaultTextColor;
			//this.parameterDeclarationColor = this.parameterAccessColor = defaultTextColor;
			this.valueKeywordColor = highlighting.GetNamedColor("NullOrValueKeywords");
			//this.externAliasKeywordColor = ...;
			
			this.parameterModifierColor = highlighting.GetNamedColor("ParameterModifiers");
			this.inactiveCodeColor = highlighting.GetNamedColor("InactiveCode");
			this.syntaxErrorColor = highlighting.GetNamedColor("SemanticError");
			
			if (document is TextDocument && SD.MainThread.CheckAccess()) {
				// Use the cache only for the live AvalonEdit document
				// Highlighting in read-only documents (e.g. search results) does
				// not need the cache as it does not need to highlight the same line multiple times
				cachedLines = new List<CachedLine>();
				// Line invalidation is only necessary for the live AvalonEdit document
				invalidLines = new List<IDocumentLine>();
				// Also, attach these event handlers only for real documents in the editor,
				// we don't need them for the highlighting in search results etc.
				SD.ParserService.ParseInformationUpdated += ParserService_ParseInformationUpdated;
				SD.ParserService.LoadSolutionProjectsThread.Finished += ParserService_LoadSolutionProjectsThreadEnded;
				eventHandlersAreRegistered = true;
				document.GetService<IServiceContainer>().AddService(typeof(CSharpSemanticHighlighter), this);
			}
		}
		
		public void Dispose()
		{
			if (eventHandlersAreRegistered) {
				SD.ParserService.ParseInformationUpdated -= ParserService_ParseInformationUpdated;
				SD.ParserService.LoadSolutionProjectsThread.Finished -= ParserService_LoadSolutionProjectsThreadEnded;
				eventHandlersAreRegistered = false;
				document.GetService<IServiceContainer>().RemoveService(typeof(CSharpSemanticHighlighter));
			}
			this.resolver = null;
			this.parseInfo = null;
		}
		#endregion
		
		#region Caching
		// If a line gets edited and we need to display it while no parse information is ready for the
		// changed file, the line would flicker (semantic highlightings disappear temporarily).
		// We avoid this issue by storing the semantic highlightings and updating them on document changes
		// (using anchor movement)
		class CachedLine
		{
			public readonly HighlightedLine HighlightedLine;
			public ITextSourceVersion OldVersion;
			
			/// <summary>
			/// Gets whether the cache line is valid (no document changes since it was created).
			/// This field gets set to false when Update() is called.
			/// </summary>
			public bool IsValid;
			
			public IDocumentLine DocumentLine { get { return HighlightedLine.DocumentLine; } }
			
			public CachedLine(HighlightedLine highlightedLine, ITextSourceVersion fileVersion)
			{
				if (highlightedLine == null)
					throw new ArgumentNullException("highlightedLine");
				if (fileVersion == null)
					throw new ArgumentNullException("fileVersion");
				
				this.HighlightedLine = highlightedLine;
				this.OldVersion = fileVersion;
				this.IsValid = true;
			}
			
			public void Update(ITextSourceVersion newVersion)
			{
				// Apply document changes to all highlighting sections:
				foreach (TextChangeEventArgs change in OldVersion.GetChangesTo(newVersion)) {
					foreach (HighlightedSection section in HighlightedLine.Sections) {
						int endOffset = section.Offset + section.Length;
						section.Offset = change.GetNewOffset(section.Offset);
						endOffset = change.GetNewOffset(endOffset);
						section.Length = endOffset - section.Offset;
					}
				}
				// The resulting sections might have become invalid:
				// - zero-length if section was deleted,
				// - a section might have moved outside the range of this document line (newline inserted in document = line split up)
				// So we will remove all highlighting sections which have become invalid.
				int lineStart = HighlightedLine.DocumentLine.Offset;
				int lineEnd = lineStart + HighlightedLine.DocumentLine.Length;
				for (int i = 0; i < HighlightedLine.Sections.Count; i++) {
					HighlightedSection section = HighlightedLine.Sections[i];
					if (section.Offset < lineStart || section.Offset + section.Length > lineEnd || section.Length <= 0)
						HighlightedLine.Sections.RemoveAt(i--);
				}
				
				this.OldVersion = newVersion;
				this.IsValid = false;
			}
		}
		#endregion
		
		#region Event Handlers
		void syntaxHighlighter_VisibleDocumentLinesChanged(object sender, EventArgs e)
		{

		}
		
		void ParserService_LoadSolutionProjectsThreadEnded(object sender, EventArgs e)
		{
			InvalidateAll();
		}
		
		void ParserService_ParseInformationUpdated(object sender, ParseInformationEventArgs e)
		{
			if (FileUtility.IsEqualFileName(e.FileName, document.FileName) && invalidLines.Count > 0) {
				cachedLines.Clear();
				foreach (IDocumentLine line in invalidLines) {
					if (!line.IsDeleted) {
						OnHighlightingStateChanged(line.LineNumber, line.LineNumber);
					}
				}
				invalidLines.Clear();
			}
		}
		#endregion
		
		#region IHighlighter implementation
		public event HighlightingStateChangedEventHandler HighlightingStateChanged;
		
		protected virtual void OnHighlightingStateChanged(int fromLineNumber, int toLineNumber)
		{
			if (HighlightingStateChanged != null) {
				HighlightingStateChanged(fromLineNumber, toLineNumber);
			}
		}
		
		IDocument IHighlighter.Document {
			get { return document; }
		}
		
		IEnumerable<HighlightingColor> IHighlighter.GetColorStack(int lineNumber)
		{
			return null;
		}
		
		void IHighlighter.UpdateHighlightingState(int lineNumber)
		{
		}
		
		public HighlightedLine HighlightLine(int lineNumber)
		{
			IDocumentLine documentLine = document.GetLineByNumber(lineNumber);
			if (hasCrashed) {
				// don't highlight anymore after we've crashed
				return new HighlightedLine(document, documentLine);
			}
			ITextSourceVersion newVersion = document.Version;
			CachedLine cachedLine = null;
			if (cachedLines != null) {
				for (int i = 0; i < cachedLines.Count; i++) {
					if (cachedLines[i].DocumentLine == documentLine) {
						if (newVersion == null || !newVersion.BelongsToSameDocumentAs(cachedLines[i].OldVersion)) {
							// cannot list changes from old to new: we can't update the cache, so we'll remove it
							cachedLines.RemoveAt(i);
						} else {
							cachedLine = cachedLines[i];
						}
						break;
					}
				}
				
				if (cachedLine != null && cachedLine.IsValid && newVersion.CompareAge(cachedLine.OldVersion) == 0) {
					// the file hasn't changed since the cache was created, so just reuse the old highlighted line
					#if DEBUG
					cachedLine.HighlightedLine.ValidateInvariants();
					#endif
					return cachedLine.HighlightedLine;
				}
			}
			
			bool wasInHighlightingGroup = inHighlightingGroup;
			if (!inHighlightingGroup) {
				BeginHighlighting();
			}
			try {
				return DoHighlightLine(lineNumber, documentLine, cachedLine, newVersion);
			} finally {
				line = null;
				if (!wasInHighlightingGroup)
					EndHighlighting();
			}
		}
		
		HighlightedLine DoHighlightLine(int lineNumber, IDocumentLine documentLine, CachedLine cachedLine, ITextSourceVersion newVersion)
		{
			if (parseInfo == null) {
				if (forceParseOnNextRefresh) {
					forceParseOnNextRefresh = false;
					parseInfo = SD.ParserService.Parse(FileName.Create(document.FileName), document) as CSharpFullParseInformation;
				} else {
					parseInfo = SD.ParserService.GetCachedParseInformation(FileName.Create(document.FileName), newVersion) as CSharpFullParseInformation;
				}
			}
			if (parseInfo == null) {
				if (invalidLines != null && !invalidLines.Contains(documentLine)) {
					invalidLines.Add(documentLine);
					//Debug.WriteLine("Semantic highlighting for line {0} - marking as invalid", lineNumber);
				}
				
				if (cachedLine != null) {
					// If there's a cached version, adjust it to the latest document changes and return it.
					// This avoids flickering when changing a line that contains semantic highlighting.
					cachedLine.Update(newVersion);
					#if DEBUG
					cachedLine.HighlightedLine.ValidateInvariants();
					#endif
					return cachedLine.HighlightedLine;
				} else {
					return null;
				}
			}
			
			if (resolver == null) {
				var compilation = SD.ParserService.GetCompilationForFile(parseInfo.FileName);
				resolver = parseInfo.GetResolver(compilation);
			}
			
			line = new HighlightedLine(document, documentLine);
			this.lineNumber = lineNumber;
			this.regionStart = new TextLocation(lineNumber, 1);
			this.regionEnd = new TextLocation(lineNumber, 1 + document.GetLineByNumber(lineNumber).Length);
			if (Debugger.IsAttached) {
				parseInfo.SyntaxTree.AcceptVisitor(this);
			} else {
				try {
					parseInfo.SyntaxTree.AcceptVisitor(this);
				} catch (Exception ex) {
					hasCrashed = true;
					throw new ApplicationException("Error highlighting line " + lineNumber, ex);
				}
			}
			//Debug.WriteLine("Semantic highlighting for line {0} - added {1} sections", lineNumber, line.Sections.Count);
			if (cachedLines != null && document.Version != null) {
				cachedLines.Add(new CachedLine(line, document.Version));
			}
			return line;
		}
		
		#if DEBUG
		public override void VisitSyntaxTree(ICSharpCode.NRefactory.CSharp.SyntaxTree syntaxTree)
		{
			base.VisitSyntaxTree(syntaxTree);
			line.ValidateInvariants();
		}
		#endif
		
		HighlightingColor IHighlighter.DefaultTextColor {
			get {
				return null;
			}
		}
		
		public void BeginHighlighting()
		{
			if (inHighlightingGroup)
				throw new InvalidOperationException();
			inHighlightingGroup = true;
			if (invalidLines == null) {
				// if invalidation isn't available, we're forced to parse the file now
				forceParseOnNextRefresh = true;
			}
		}
		
		public void EndHighlighting()
		{
			inHighlightingGroup = false;
			this.resolver = null;
			this.parseInfo = null;
			
			// TODO use this to remove cached lines which are no longer visible
//			var visibleDocumentLines = new HashSet<IDocumentLine>(syntaxHighlighter.GetVisibleDocumentLines());
//			cachedLines.RemoveAll(c => !visibleDocumentLines.Contains(c.DocumentLine));
		}
		
		public HighlightingColor GetNamedColor(string name)
		{
			return null;
		}
		#endregion
		
		#region Colorize
		protected override void Colorize(TextLocation start, TextLocation end, HighlightingColor color)
		{
			if (color == null)
				return;
			if (start.Line <= lineNumber && end.Line >= lineNumber) {
				int lineStartOffset = line.DocumentLine.Offset;
				int lineEndOffset = lineStartOffset + line.DocumentLine.Length;
				int startOffset = lineStartOffset + (start.Line == lineNumber ? start.Column - 1 : 0);
				int endOffset = lineStartOffset + (end.Line == lineNumber ? end.Column - 1 : line.DocumentLine.Length);
				// For some parser errors, the mcs parser produces grossly wrong locations (e.g. miscounting the number of newlines),
				// so we need to coerce the offsets to valid values within the line
				startOffset = startOffset.CoerceValue(lineStartOffset, lineEndOffset);
				endOffset = endOffset.CoerceValue(lineStartOffset, lineEndOffset);
				if (line.Sections.Count > 0) {
					HighlightedSection prevSection = line.Sections.Last();
					if (startOffset < prevSection.Offset + prevSection.Length) {
						// The mcs parser sometimes creates strange ASTs with duplicate nodes
						// when there are syntax errors (e.g. "int A() public static void Main() {}"),
						// so we'll silently ignore duplicate colorization.
						return;
						//throw new InvalidOperationException("Cannot create unordered highlighting section");
					}
				}
				line.Sections.Add(new HighlightedSection {
				                  	Offset = startOffset,
				                  	Length = endOffset - startOffset,
				                  	Color = color
				                  });
			}
		}
		
		protected override void Colorize(AstNode node, HighlightingColor color)
		{
			if (currentSymbolReference != null && currentNavigator == null)
				currentNavigator = InitNavigator();

			if (currentNavigator != null) {
				var resolverNode = node;
				while (CSharpAstResolver.IsUnresolvableNode(resolverNode) && resolverNode.Parent != null)
					resolverNode = resolverNode.Parent;
				if (resolverNode.Role == Roles.TargetExpression && resolverNode.Parent is InvocationExpression)
					resolverNode = resolverNode.Parent;
				
				if (node is Identifier)
					resolverNode.AddAnnotation(node);
				if (color != null)
					resolverNode.AddAnnotation(color);
				currentNavigator.Resolved(resolverNode, resolver.Resolve(resolverNode));
			}
			base.Colorize(node, color);
		}
		
		protected override void Colorize(Identifier identifier, ResolveResult rr)
		{
			if (currentSymbolReference != null && currentNavigator == null)
				currentNavigator = InitNavigator();

			if (currentNavigator != null) {
				currentNavigator.Resolved(identifier, rr);
			}
			base.Colorize(identifier, rr);
		}
		
		public override void VisitPrimitiveType(PrimitiveType primitiveType)
		{
			// highlight usages of primitive types as well.
			Colorize(primitiveType, null);
		}
		
		public readonly Color DefaultFillColor = Color.FromArgb(22, 30, 130, 255);

		public void SetCurrentSymbol(ISymbol symbol)
		{
			currentNavigator = null;
			currentSymbolReference = null;
			if (symbol != null)
				currentSymbolReference = symbol.ToReference();
			InvalidateAll();
		}

		void InvalidateAll()
		{
			cachedLines.Clear();
			invalidLines.Clear();
			forceParseOnNextRefresh = true;
			OnHighlightingStateChanged(1, document.LineCount);
		}
		
		FindReferences findReferences = new FindReferences();
		
		IResolveVisitorNavigator InitNavigator()
		{
			if (currentSymbolReference == null) return null;
			var compilation = resolver.Compilation;
			var symbol = currentSymbolReference.Resolve(compilation.TypeResolveContext);
			var searchScopes = findReferences.GetSearchScopes(symbol);
			if (searchScopes.Count == 0)
				return null;
			var navigators = new IResolveVisitorNavigator[searchScopes.Count];
			for (int i = 0; i < navigators.Length; i++) {
				navigators[i] = searchScopes[i].GetNavigator(compilation, ColorizeMatch);
			}
			IResolveVisitorNavigator combinedNavigator;
			if (searchScopes.Count == 1) {
				combinedNavigator = navigators[0];
			} else {
				combinedNavigator = new CompositeResolveVisitorNavigator(navigators);
			}
			
			return combinedNavigator;
		}
		
		void ColorizeMatch(AstNode node, ResolveResult result)
		{
			Identifier identifier = node.Annotation<Identifier>() ?? node.GetChildByRole(Roles.Identifier);
			TextLocation start, end;
			if (!identifier.IsNull) {
				start = identifier.StartLocation;
				end = identifier.EndLocation;
			} else {
				start = node.StartLocation;
				end = node.EndLocation;
			}
			var complementary = node.Annotation<HighlightingColor>();
			HighlightingColor newColor = symbolReferenceColor;
			if (complementary != null) {
				newColor = newColor.Clone();
				newColor.MergeWith(complementary);
			}
			Colorize(start, end, newColor);
		}
		#endregion
	}
}
