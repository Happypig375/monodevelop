//
// MacObjectValueTreeView.cs
//
// Author:
//       Jeffrey Stedfast <jestedfa@microsoft.com>
//
// Copyright (c) 2019 Microsoft Corp.
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.

using System;
using System.Text;
using System.Collections.Generic;

using AppKit;
using Foundation;
using CoreGraphics;

using Xwt.Drawing;

using MonoDevelop.Ide;
using MonoDevelop.Core;
using MonoDevelop.Components;
using MonoDevelop.Ide.Commands;
using MonoDevelop.Components.Commands;

namespace MonoDevelop.Debugger
{
	/// <summary>
	/// NSObject wrapper for data items in the Cocoa implementation of the ObjectValueTreeView.
	/// </summary>
	class MacObjectValueNode : NSObject
	{
		public readonly List<MacObjectValueNode> Children = new List<MacObjectValueNode> ();
		public readonly MacObjectValueNode Parent;
		public readonly ObjectValueNode Target;
		public bool HideValueButton;

		public MacObjectValueNode (MacObjectValueNode parent, ObjectValueNode target)
		{
			Parent = parent;
			Target = target;
		}
	}

	/// <summary>
	/// The data source for the Cocoa implementation of the ObjectValueTreeView.
	/// </summary>
	class MacObjectValueTreeViewDataSource : NSOutlineViewDataSource
	{
		readonly Dictionary<ObjectValueNode, MacObjectValueNode> mapping = new Dictionary<ObjectValueNode, MacObjectValueNode> ();
		readonly MacObjectValueTreeView treeView;
		MacObjectValueNode root;

		public MacObjectValueTreeViewDataSource (MacObjectValueTreeView treeView)
		{
			this.treeView = treeView;
		}

		public ObjectValueNode Root {
			get { return root.Target; }
			set {
				foreach (var kvp in mapping)
					kvp.Value.Dispose ();

				mapping.Clear ();

				root = new MacObjectValueNode (null, value);
				mapping.Add (value, root);

				foreach (var child in value.Children)
					Add (root, child);

				if (treeView.AllowWatchExpressions)
					Add (root, new AddNewExpressionObjectValueNode ());
			}
		}

		public bool TryGetValue (ObjectValueNode node, out MacObjectValueNode value)
		{
			return mapping.TryGetValue (node, out value);
		}

		void Add (MacObjectValueNode parent, ObjectValueNode node)
		{
			var value = new MacObjectValueNode (parent, node);
			mapping[node] = value;

			parent.Children.Add (value);

			foreach (var child in node.Children)
				Add (value, child);

			if (node.HasChildren && !node.ChildrenLoaded)
				Add (value, new LoadingObjectValueNode (node));
		}

		void Insert (MacObjectValueNode parent, int index, ObjectValueNode node)
		{
			var value = new MacObjectValueNode (parent, node);
			mapping[node] = value;

			parent.Children.Insert (index, value);

			foreach (var child in node.Children)
				Add (value, child);

			if (node.HasChildren && !node.ChildrenLoaded)
				Add (value, new LoadingObjectValueNode (node));
		}

		//void Remove (ObjectValueNode node)
		//{
		//	foreach (var child in node.Children)
		//		Remove (child);

		//	if (mapping.TryGetValue (node, out var value))
		//		value.Dispose ();

		//	mapping.Remove (node);
		//}

		public void Replace (ObjectValueNode node, ObjectValueNode[] replacementNodes)
		{
			if (!TryGetValue (node, out var item))
				return;

			var parent = item.Parent;
			int index = -1;

			for (int i = 0; i < parent.Children.Count; i++) {
				if (parent.Children[i] == item) {
					index = i;
					break;
				}
			}

			if (index == -1)
				return;

			parent.Children.RemoveAt (index);
			mapping.Remove (item.Target);
			item.Dispose ();

			var indexes = new NSIndexSet (index);

			if (parent.Target is RootObjectValueNode)
				treeView.RemoveItems (indexes, null, NSTableViewAnimation.None);
			else
				treeView.RemoveItems (indexes, parent, NSTableViewAnimation.None);

			if (replacementNodes.Length > 0) {
				for (int i = 0; i < replacementNodes.Length; i++)
					Insert (parent, index + i, replacementNodes[i]);

				var range = new NSRange (index, replacementNodes.Length);
				indexes = NSIndexSet.FromNSRange (range);

				if (parent.Target is RootObjectValueNode)
					treeView.InsertItems (indexes, null, NSTableViewAnimation.None);
				else
					treeView.InsertItems (indexes, parent, NSTableViewAnimation.None);
			}
		}

		public void ReloadChildren (ObjectValueNode node)
		{
			if (!TryGetValue (node, out var parent))
				return;

			NSIndexSet indexes;
			NSRange range;

			if (parent.Children.Count > 0) {
				range = new NSRange (0, parent.Children.Count);
				indexes = NSIndexSet.FromNSRange (range);

				foreach (var child in parent.Children) {
					mapping.Remove (child.Target);
					child.Dispose ();
				}

				parent.Children.Clear ();

				if (parent.Target is RootObjectValueNode)
					treeView.RemoveItems (indexes, null, NSTableViewAnimation.None);
				else
					treeView.RemoveItems (indexes, parent, NSTableViewAnimation.None);
			}

			for (int i = 0; i < node.Children.Count; i++)
				Add (parent, node.Children[i]);

			// if we did not load all the children, add a Show More node
			if (!node.ChildrenLoaded)
				Add (parent, new ShowMoreValuesObjectValueNode (node));

			range = new NSRange (0, parent.Children.Count);
			indexes = NSIndexSet.FromNSRange (range);

			if (parent.Target is RootObjectValueNode)
				treeView.InsertItems (indexes, null, NSTableViewAnimation.None);
			else
				treeView.InsertItems (indexes, parent, NSTableViewAnimation.None);
		}

		public override nint GetChildrenCount (NSOutlineView outlineView, NSObject item)
		{
			var node = (item as MacObjectValueNode) ?? root;

			if (node == null)
				return 0;

			return node.Children.Count;
		}

		public override NSObject GetChild (NSOutlineView outlineView, nint childIndex, NSObject item)
		{
			var node = (item as MacObjectValueNode) ?? root;

			if (node == null || childIndex >= node.Children.Count)
				return null;

			return node.Children[(int) childIndex];
		}

		public override bool ItemExpandable (NSOutlineView outlineView, NSObject item)
		{
			var node = (item as MacObjectValueNode) ?? root;

			return node != null && node.Children.Count > 0;
		}

		protected override void Dispose (bool disposing)
		{
			if (disposing) {
				foreach (var kvp in mapping)
					kvp.Value.Dispose ();
				mapping.Clear ();
				root = null;
			}

			base.Dispose (disposing);
		}
	}

	/// <summary>
	/// The worker delegate for the Cocoa implementation of the ObjectValueTreeView.
	/// </summary>
	class MacObjectValueTreeViewDelegate : NSOutlineViewDelegate
	{
		static readonly NSString NSObjectKey = new NSString ("NSObject");
		readonly MacObjectValueTreeView treeView;

		public MacObjectValueTreeViewDelegate (MacObjectValueTreeView treeView)
		{
			this.treeView = treeView;
		}

		public override NSView GetView (NSOutlineView outlineView, NSTableColumn tableColumn, NSObject item)
		{
			var view = (MacDebuggerObjectCellViewBase) outlineView.MakeView (tableColumn.Identifier, this);

			switch (tableColumn.Identifier) {
			case "name":
				if (view == null)
					view = new MacDebuggerObjectNameView (treeView);
				break;
			case "value":
				if (view == null)
					view = new MacDebuggerObjectValueView (treeView);
				break;
			case "type":
				if (view == null)
					view = new MacDebuggerObjectTypeView (treeView);
				break;
			case "pin":
				if (view == null)
					view = new MacDebuggerObjectPinView (treeView);
				break;
			default:
				return null;
			}

			view.Row = outlineView.RowForItem (item);
			view.ObjectValue = item;

			return view;
		}

		public override void ItemDidCollapse (NSNotification notification)
		{
			//var outlineView = (NSOutlineView) notification.Object;

			if (!notification.UserInfo.TryGetValue (NSObjectKey, out var value))
				return;

			var node = (value as MacObjectValueNode)?.Target;

			if (node == null)
				return;

			treeView.CollapseNode (node);
			treeView.CompactColumns ();
			treeView.Resize ();
		}

		public override void ItemDidExpand (NSNotification notification)
		{
			//var outlineView = (NSOutlineView) notification.Object;

			if (!notification.UserInfo.TryGetValue (NSObjectKey, out var value))
				return;

			var node = value as MacObjectValueNode;

			if (node == null)
				return;

			treeView.CompactColumns ();

			node.HideValueButton = true;
			treeView.ReloadItem (node, false);

			treeView.ExpandNode (node.Target);
			treeView.Resize ();
		}

		public override bool ShouldExpandItem (NSOutlineView outlineView, NSObject item)
		{
			if (!treeView.AllowExpanding)
				return false;

			var node = (item as MacObjectValueNode)?.Target;

			return node != null && node.HasChildren;
		}

		public event EventHandler SelectionChanged;

		public override void SelectionDidChange (NSNotification notification)
		{
			SelectionChanged?.Invoke (this, EventArgs.Empty);
		}
	}

	abstract class MacDebuggerObjectCellViewBase : NSTableCellView
	{
		protected const double XPadding = 2.0;

		protected MacDebuggerObjectCellViewBase (MacObjectValueTreeView treeView, string identifier)
		{
			Identifier = identifier;
			TreeView = treeView;
		}

		protected MacDebuggerObjectCellViewBase (IntPtr handle) : base (handle)
		{
		}

		protected MacObjectValueTreeView TreeView {
			get; private set;
		}

		public override NSObject ObjectValue {
			get { return base.ObjectValue; }
			set {
				var target = ((MacObjectValueNode) value)?.Target;

				if (Node != target) {
					if (target != null)
						target.ValueChanged += OnValueChanged;

					if (Node != null)
						Node.ValueChanged -= OnValueChanged;

					Node = target;
				}

				base.ObjectValue = value;

				if (Superview != null)
					UpdateContents ();
			}
		}

		public ObjectValueNode Node {
			get; private set;
		}

		public nint Row {
			get; set;
		}

		public bool IsShowMoreValues {
			get { return Node is ShowMoreValuesObjectValueNode; }
		}

		public bool IsLoading {
			get { return Node is LoadingObjectValueNode; }
		}

		protected static NSImage GetImage (string name, Gtk.IconSize size)
		{
			var icon = ImageService.GetIcon (name, size);

			return icon.ToNSImage ();
		}

		protected static NSImage GetImage (string name, Gtk.IconSize size, double alpha)
		{
			var icon = ImageService.GetIcon (name, size).WithAlpha (alpha);

			return icon.ToNSImage ();
		}

		protected static NSImage GetImage (string name, int width, int height)
		{
			var icon = ImageService.GetIcon (name).WithSize (width, height);

			return icon.ToNSImage ();
		}

		protected void UpdateXPosition (NSView view, double x)
		{
			var frame = view.Frame;

			view.Frame = new CGRect (x, frame.Y, frame.Width, frame.Height);
		}

		public override void ViewDidMoveToSuperview ()
		{
			base.ViewDidMoveToSuperview ();
			UpdateContents ();
		}

		public override NSBackgroundStyle BackgroundStyle {
			get { return base.BackgroundStyle; }
			set {
				base.BackgroundStyle = value;
				UpdateContents ();
			}
		}

		protected abstract void UpdateContents ();

		public void Refresh ()
		{
			UpdateContents ();
			SetNeedsDisplayInRect (Frame);
		}

		void OnValueChanged (object sender, EventArgs e)
		{
			Refresh ();
		}

		protected override void Dispose (bool disposing)
		{
			if (disposing && Node != null) {
				Node.ValueChanged -= OnValueChanged;
				Node = null;
			}

			base.Dispose (disposing);
		}
	}

	/// <summary>
	/// The NSTableViewCell used for the "Name" column.
	/// </summary>
	class MacDebuggerObjectNameView : MacDebuggerObjectCellViewBase
	{
		readonly NSColor defaultTextColor;
		PreviewButtonIcon currentIcon;
		bool previewIconVisible;
		bool textChanged;
		bool disposed;

		public MacDebuggerObjectNameView (MacObjectValueTreeView treeView) : base (treeView, "name")
		{
			ImageView = new NSImageView (new CGRect (0, 0, 16, 16));

			TextField = new NSTextField (new CGRect (16 + XPadding, 0, 128, 16)) {
				AutoresizingMask = NSViewResizingMask.WidthSizable,
				BackgroundColor = NSColor.Clear,
				Bordered = false,
				Editable = false
			};
			defaultTextColor = TextField.TextColor;
			TextField.EditingBegan += OnEditingBegan;
			TextField.EditingEnded += OnEditingEnded;
			TextField.Changed += OnTextChanged;

			AddSubview (ImageView);
			AddSubview (TextField);

			PreviewButton = new NSButton (new CGRect (0, 0, 16, 16)) {
				AutoresizingMask = NSViewResizingMask.NotSizable,
				BezelStyle = NSBezelStyle.Inline,
				Bordered = false
			};
			PreviewButton.Activated += OnPreviewButtonClicked;
		}

		public MacDebuggerObjectNameView (IntPtr handle) : base (handle)
		{
		}

		public NSButton PreviewButton {
			get; private set;
		}

		protected override void UpdateContents ()
		{
			if (Node == null)
				return;

			var iconName = ObjectValueTreeViewController.GetIcon (Node.Flags);
			ImageView.Image = GetImage (iconName, Gtk.IconSize.Menu);

			var placeholder = string.Empty;
			var color = default (Color);
			var name = Node.Name;

			if (Node.IsUnknown) {
				if (TreeView.DebuggerService.Frame != null)
					color = Styles.ObjectValueTreeValueDisabledText;
			} else if (Node.IsError || Node.IsNotSupported) {
			} else if (Node.IsImplicitNotSupported) {
			} else if (Node.IsEvaluating) {
				if (Node.GetIsEvaluatingGroup ()) {
					color = Styles.ObjectValueTreeValueDisabledText;
					name = Node.Name;
				}
			} else if (Node.IsEnumerable) {
			} else {
				if (Node is AddNewExpressionObjectValueNode) {
					placeholder = GettextCatalog.GetString ("Add new expression");
					name = string.Empty;
				} else if (TreeView.Controller.GetNodeHasChangedSinceLastCheckpoint (Node)) {
					color = Styles.ObjectValueTreeValueModifiedText;
				}
			}

			if (color != default)
				TextField.TextColor = NSColor.FromCGColor (new CGColor ((nfloat) color.Red, (nfloat) color.Green, (nfloat) color.Blue));
			else
				TextField.TextColor = defaultTextColor;
			TextField.Editable = TreeView.AllowWatchExpressions;
			TextField.PlaceholderString = placeholder;
			TextField.StringValue = name;
			TextField.SizeToFit ();

			if (MacObjectValueTreeView.ValidObjectForPreviewIcon (Node)) {
				SetPreviewButtonIcon (PreviewButtonIcon.Hidden);
				var x = TextField.Frame.X + TextField.Frame.Width + XPadding;
				UpdateXPosition (PreviewButton, x);

				if (!previewIconVisible) {
					AddSubview (PreviewButton);
					previewIconVisible = true;
				}
			} else {
				PreviewButton.RemoveFromSuperview ();
				previewIconVisible = false;
			}
		}

		public void SetPreviewButtonIcon (PreviewButtonIcon icon)
		{
			if (!previewIconVisible || icon == currentIcon)
				return;

			var name = ObjectValueTreeViewController.GetPreviewButtonIcon (icon);
			PreviewButton.Image = GetImage (name, Gtk.IconSize.Menu);
			currentIcon = icon;

			SetNeedsDisplayInRect (PreviewButton.Frame);
		}

		void OnPreviewButtonClicked (object sender, EventArgs e)
		{
			if (!TreeView.DebuggerService.CanQueryDebugger || PreviewWindowManager.IsVisible)
				return;

			if (!MacObjectValueTreeView.ValidObjectForPreviewIcon (Node))
				return;

			var bounds = ConvertRectFromView (PreviewButton.Bounds, PreviewButton);
			var buttonArea = new Gdk.Rectangle ((int) bounds.X, (int) bounds.Y, (int) bounds.Width, (int) bounds.Height);
			var val = Node.GetDebuggerObjectValue ();

			SetPreviewButtonIcon (PreviewButtonIcon.Active);

			// FIXME: this crashes because Mac native widgets aren't handled...
			//TreeView.DebuggingService.ShowPreviewVisualizer (val, this, buttonArea);
		}

		void OnEditingBegan (object sender, EventArgs e)
		{
			TreeView.OnStartEditing ();
		}

		void OnEditingEnded (object sender, EventArgs e)
		{
			TreeView.OnEndEditing ();

			if (!textChanged)
				return;

			textChanged = false;

			var expression = TextField.StringValue.Trim ();

			if (Node is AddNewExpressionObjectValueNode) {
				if (expression.Length > 0)
					TreeView.OnExpressionAdded (expression);
			} else {
				TreeView.OnExpressionEdited (Node, expression);
			}
		}

		void OnTextChanged (object sender, EventArgs e)
		{
			textChanged = true;
		}

		protected override void Dispose (bool disposing)
		{
			if (disposing && !disposed) {
				PreviewButton.Activated -= OnPreviewButtonClicked;
				TextField.EditingBegan -= OnEditingBegan;
				TextField.EditingEnded -= OnEditingEnded;
				TextField.Changed -= OnTextChanged;
				disposed = true;
			}

			base.Dispose (disposing);
		}
	}

	/// <summary>
	/// The NSTableViewCell used for the "Value" column.
	/// </summary>
	class MacDebuggerObjectValueView : MacDebuggerObjectCellViewBase
	{
		readonly NSColor defaultTextColor;
		NSImageView statusIcon;
		bool statusIconVisible;
		NSImageView colorPreview;
		bool colorPreviewVisible;
		NSButton valueButton;
		bool valueButtonVisible;
		NSButton viewerButton;
		bool viewerButtonVisible;
		bool textChanged;
		bool disposed;

		public MacDebuggerObjectValueView (MacObjectValueTreeView treeView) : base (treeView, "value")
		{
			statusIcon = new NSImageView (new CGRect (0, 0, 16, 16));

			colorPreview = new NSImageView (new CGRect (0, 0, 16, 16));

			valueButton = new NSButton (new CGRect (0, 1, 72, 12)) {
				Title = GettextCatalog.GetString (""),
				BezelStyle = NSBezelStyle.Inline
			};
			valueButton.Font = NSFont.FromDescription (valueButton.Font.FontDescriptor, valueButton.Font.PointSize - 3);
			valueButton.Activated += OnValueButtonActivated;

			if (treeView.CompactView) {
				viewerButton = new NSButton (new CGRect (0, 2, 12, 12)) {
					Image = GetImage (Gtk.Stock.Edit, 12, 12)
				};
			} else {
				viewerButton = new NSButton (new CGRect (0, 0, 16, 16)) {
					Image = GetImage (Gtk.Stock.Edit, Gtk.IconSize.Menu)
				};
			}
			viewerButton.Bordered = false;
			viewerButton.BezelStyle = NSBezelStyle.Inline;
			viewerButton.Activated += OnViewerButtonActivated;

			TextField = new NSTextField (new CGRect (0, 0, 128, 16)) {
				AutoresizingMask = NSViewResizingMask.WidthSizable,
				BackgroundColor = NSColor.Clear,
				Bordered = false,
				Editable = false
			};
			TextField.EditingBegan += OnEditingBegan;
			TextField.EditingEnded += OnEditingEnded;
			TextField.Changed += OnTextChanged;

			defaultTextColor = TextField.TextColor;

			AddSubview (TextField);
		}

		public MacDebuggerObjectValueView (IntPtr handle) : base (handle)
		{
		}

		protected override void UpdateContents ()
		{
			if (Node == null)
				return;

			var color = default (Color);
			var editable = TreeView.AllowEditing;
			string evaluateStatusIcon = null;
			var showViewerButton = false;
			string valueButtonText = null;
			string strval;
			double x = 0;

			if (Node.IsUnknown) {
				if (TreeView.DebuggerService.Frame != null) {
					strval = GettextCatalog.GetString ("The name '{0}' does not exist in the current context.", Node.Name);
				} else {
					strval = string.Empty;
				}
				evaluateStatusIcon = Ide.Gui.Stock.Warning;
			} else if (Node.IsError || Node.IsNotSupported) {
				evaluateStatusIcon = Ide.Gui.Stock.Warning;
				strval = Node.Value;
				int i = strval.IndexOf ('\n');
				if (i != -1)
					strval = strval.Substring (0, i);
				color = Styles.ObjectValueTreeValueErrorText;
			} else if (Node.IsImplicitNotSupported) {
				strval = "";//val.Value; with new "Show Value" button we don't want to display message "Implicit evaluation is disabled"
				color = Styles.ObjectValueTreeValueDisabledText;
				if (Node.CanRefresh)
					valueButtonText = GettextCatalog.GetString ("Show Value");
			} else if (Node.IsEvaluating) {
				strval = GettextCatalog.GetString ("Evaluating\u2026");

				evaluateStatusIcon = "md-spinner-16";

				color = Styles.ObjectValueTreeValueDisabledText;
			} else if (Node.IsEnumerable) {
				if (Node is ShowMoreValuesObjectValueNode) {
					valueButtonText = GettextCatalog.GetString ("Show More");
				} else {
					valueButtonText = GettextCatalog.GetString ("Show Values");
				}
				strval = "";
			} else if (Node is AddNewExpressionObjectValueNode) {
				strval = string.Empty;
				editable = false;
			} else {
				strval = TreeView.Controller.GetDisplayValueWithVisualisers (Node, out showViewerButton);

				if (TreeView.Controller.GetNodeHasChangedSinceLastCheckpoint (Node))
					color = Styles.ObjectValueTreeValueModifiedText;
			}

			strval = strval.Replace ("\r\n", " ").Replace ("\n", " ");

			// First item: Status Icon
			if (evaluateStatusIcon != null) {
				statusIcon.Image = GetImage (evaluateStatusIcon, Gtk.IconSize.Menu);
				UpdateXPosition (statusIcon, x);
				x += statusIcon.Frame.Width + XPadding;

				if (!statusIconVisible) {
					AddSubview (statusIcon);
					statusIconVisible = true;
				}
			} else if (statusIconVisible) {
				statusIcon.RemoveFromSuperview ();
				statusIconVisible = false;
			}

			// Second Item: Color Preview
			// TODO:

			// Third Item: Value Button
			if (valueButtonText != null && !((MacObjectValueNode) ObjectValue).HideValueButton) {
				valueButton.Title = valueButtonText;
				valueButton.SizeToFit ();

				UpdateXPosition (valueButton, x);
				x += valueButton.Frame.Width + XPadding;

				if (!valueButtonVisible) {
					AddSubview (valueButton);
					valueButtonVisible = true;
				}
			} else if (valueButtonVisible) {
				valueButton.RemoveFromSuperview ();
				valueButtonVisible = false;
			}

			// Fourth Item: Viewer Button
			if (showViewerButton) {
				UpdateXPosition (viewerButton, x);
				x += viewerButton.Frame.Width + XPadding;

				if (!viewerButtonVisible) {
					AddSubview (viewerButton);
					viewerButtonVisible = true;
				}
			} else if (viewerButtonVisible) {
				viewerButton.RemoveFromSuperview ();
				viewerButtonVisible = false;
			}

			if (color != default)
				TextField.TextColor = NSColor.FromCGColor (new CGColor ((nfloat) color.Red, (nfloat) color.Green, (nfloat) color.Blue));
			else
				TextField.TextColor = defaultTextColor;
			TextField.Editable = editable;
			TextField.StringValue = strval;
			UpdateXPosition (TextField, x);
		}

		void OnEditingBegan (object sender, EventArgs e)
		{
			TreeView.OnStartEditing ();
		}

		void OnEditingEnded (object sender, EventArgs e)
		{
			TreeView.OnEndEditing ();

			if (!textChanged)
				return;

			textChanged = false;

			var newValue = TextField.StringValue;

			if (TreeView.GetEditValue (Node, newValue))
				Refresh ();
		}

		void OnTextChanged (object sender, EventArgs e)
		{
			textChanged = true;
		}

		void OnValueButtonActivated (object sender, EventArgs e)
		{
			if (Node.IsEnumerable) {
				if (Node is ShowMoreValuesObjectValueNode moreNode) {
					TreeView.LoadMoreChildren (moreNode.EnumerableNode);
				} else {
					// use ExpandItem to expand so we see the loading message, expanding the node will trigger a fetch of the children
					TreeView.ExpandItem (ObjectValue, false);
				}
			} else {
				// this is likely to support IsImplicitNotSupported
				TreeView.Refresh (Node);
			}

			((MacObjectValueNode) ObjectValue).HideValueButton = true;
			Refresh ();
		}

		void OnViewerButtonActivated (object sender, EventArgs e)
		{
			if (!TreeView.DebuggerService.CanQueryDebugger)
				return;

			if (TreeView.ShowVisualizer (Node))
				Refresh ();
		}

		protected override void Dispose (bool disposing)
		{
			if (disposing && !disposed) {
				viewerButton.Activated -= OnViewerButtonActivated;
				valueButton.Activated -= OnValueButtonActivated;
				TextField.EditingBegan -= OnEditingBegan;
				TextField.EditingEnded -= OnEditingEnded;
				TextField.Changed -= OnTextChanged;
				disposed = true;
			}

			base.Dispose (disposing);
		}
	}

	/// <summary>
	/// The NSTableViewCell used for the "Type" column.
	/// </summary>
	class MacDebuggerObjectTypeView : MacDebuggerObjectCellViewBase
	{
		public MacDebuggerObjectTypeView (MacObjectValueTreeView treeView) : base (treeView, "type")
		{
			TextField = new NSTextField (new CGRect (0, 0, 128, 16)) {
				AutoresizingMask = NSViewResizingMask.WidthSizable,
				BackgroundColor = NSColor.Clear,
				Bordered = false,
				Editable = false
			};

			AddSubview (TextField);
		}

		public MacDebuggerObjectTypeView (IntPtr handle) : base (handle)
		{
		}

		protected override void UpdateContents ()
		{
			if (Node == null)
				return;

			TextField.StringValue = Node.TypeName;
		}
	}

	class MacDebuggerObjectPinView : MacDebuggerObjectCellViewBase
	{
		static readonly NSImage unpinnedImage = GetImage ("md-pin-up", Gtk.IconSize.Menu);
		static readonly NSImage pinnedImage = GetImage ("md-pin-down", Gtk.IconSize.Menu);
		static readonly NSImage liveUpdateOnImage = GetImage ("md-live", Gtk.IconSize.Menu);
		static readonly NSImage liveUpdateOffImage = GetImage ("md-live", Gtk.IconSize.Menu, 0.5);
		static readonly NSImage none = GetImage ("md-empty", Gtk.IconSize.Menu);
		bool disposed;
		bool pinned;

		public MacDebuggerObjectPinView (MacObjectValueTreeView treeView) : base (treeView, "pin")
		{
			PinButton = new NSButton (new CGRect (0, 0, 16, 16)) {
				AutoresizingMask = NSViewResizingMask.NotSizable,
				BezelStyle = NSBezelStyle.Inline,
				Image = none,
				Bordered = false,
			};
			PinButton.Activated += OnPinButtonClicked;
			AddSubview (PinButton);

			LiveUpdateButton = new NSButton (new CGRect (18, 0, 16, 16)) {
				AutoresizingMask = NSViewResizingMask.NotSizable,
				BezelStyle = NSBezelStyle.Inline,
				Image = liveUpdateOffImage,
				Bordered = false
			};
			LiveUpdateButton.Activated += OnLiveUpdateButtonClicked;
			AddSubview (LiveUpdateButton);
		}

		public MacDebuggerObjectPinView (IntPtr handle) : base (handle)
		{
		}

		public NSButton PinButton {
			get; private set;
		}

		public NSButton LiveUpdateButton {
			get; private set;
		}

		protected override void UpdateContents ()
		{
			if (Node == null)
				return;

			if (TreeView.PinnedWatch != null && Node.Parent == TreeView.Controller.Root) {
				PinButton.Image = pinnedImage;
				pinned = true;
			} else {
				PinButton.Image = none;
				pinned = false;
			}

			if (pinned) {
				if (TreeView.PinnedWatch.LiveUpdate)
					LiveUpdateButton.Image = liveUpdateOnImage;
				else
					LiveUpdateButton.Image = liveUpdateOffImage;
			} else {
				LiveUpdateButton.Image = none;
			}
		}

		void OnPinButtonClicked (object sender, EventArgs e)
		{
			if (pinned) {
				TreeView.Unpin (Node);
			} else {
				TreeView.Pin (Node);
			}
		}

		void OnLiveUpdateButtonClicked (object sender, EventArgs e)
		{
			if (pinned) {
				DebuggingService.SetLiveUpdateMode (TreeView.PinnedWatch, !TreeView.PinnedWatch.LiveUpdate);
				Refresh ();
			}
		}

		public void SetMouseHover (bool hover)
		{
			if (pinned)
				return;

			PinButton.Image = hover ? unpinnedImage : none;
			SetNeedsDisplayInRect (PinButton.Frame);
		}

		protected override void Dispose (bool disposing)
		{
			if (disposing && !disposed) {
				LiveUpdateButton.Activated -= OnLiveUpdateButtonClicked;
				PinButton.Activated -= OnPinButtonClicked;
				disposed = true;
			}

			base.Dispose (disposing);
		}
	}

	public class MacObjectValueTreeView : NSOutlineView, IObjectValueTreeView /*, ICompletionWidget*/
	{
		const int MinimumColumnWidth = 38;

		MacObjectValueTreeViewDelegate treeViewDelegate;
		MacObjectValueTreeViewDataSource dataSource;

		readonly NSTableColumn nameColumn;
		readonly NSTableColumn valueColumn;
		readonly NSTableColumn typeColumn;
		readonly NSTableColumn pinColumn;
		readonly bool allowPopupMenu;
		readonly bool rootPinVisible;
		readonly bool compactView;

		PinnedWatch pinnedWatch;

		double nameColumnWidth;
		double valueColumnWidth;
		double typeColumnWidth;

		PreviewButtonIcon currentHoverIcon;
		nint currentHoverRow = -1;

		bool allowWatchExpressions;
		bool allowEditing;
		bool disposed;

		public MacObjectValueTreeView (
			IObjectValueDebuggerService debuggerService,
			ObjectValueTreeViewController controller,
			bool allowEditing,
			bool headersVisible,
			bool allowWatchExpressions,
			bool compactView,
			bool allowPinning,
			bool allowPopupMenu,
			bool rootPinVisible)
		{
			DebuggerService = debuggerService;
			Controller = controller;

			this.allowWatchExpressions = allowWatchExpressions;
			this.rootPinVisible = rootPinVisible;
			this.allowPopupMenu = allowPopupMenu;
			this.allowEditing = allowEditing;
			this.compactView = compactView;
			ResetColumnSizes ();

			Delegate = treeViewDelegate = new MacObjectValueTreeViewDelegate (this);
			DataSource = dataSource = new MacObjectValueTreeViewDataSource (this);
			ColumnAutoresizingStyle = NSTableViewColumnAutoresizingStyle.Sequential;
			treeViewDelegate.SelectionChanged += OnSelectionChanged;
			UsesAlternatingRowBackgroundColors = true;
			FocusRingType = NSFocusRingType.None;
			AutoresizesOutlineColumn = false;
			AllowsColumnResizing = true;

			nameColumn = new NSTableColumn ("name") { Editable = controller.AllowWatchExpressions, MinWidth = MinimumColumnWidth, ResizingMask = NSTableColumnResizing.None };
			nameColumn.Title = GettextCatalog.GetString ("Name");
			AddColumn (nameColumn);

			OutlineTableColumn = nameColumn;

			valueColumn = new NSTableColumn ("value") { Editable = controller.AllowEditing, MinWidth = MinimumColumnWidth, ResizingMask = NSTableColumnResizing.None };
			valueColumn.Title = GettextCatalog.GetString ("Value");
			if (compactView)
				valueColumn.MaxWidth = 800;
			AddColumn (valueColumn);

			if (!compactView) {
				typeColumn = new NSTableColumn ("type") { Editable = false, MinWidth = MinimumColumnWidth, ResizingMask = NSTableColumnResizing.None };
				typeColumn.Title = GettextCatalog.GetString ("Type");
				AddColumn (typeColumn);
			}

			if (allowPinning) {
				pinColumn = new NSTableColumn ("pin") { Editable = false, MinWidth = 34, MaxWidth = 34, Width = 34, ResizingMask = NSTableColumnResizing.None };
				AddColumn (pinColumn);
			}

			if (!headersVisible)
				HeaderView = null;

			AdjustColumnSizes ();
		}

		public ObjectValueTreeViewController Controller {
			get; private set;
		}

		public bool CompactView {
			get { return compactView; }
		}

		public IObjectValueDebuggerService DebuggerService {
			get; private set;
		}

		/// <summary>
		/// Gets a value indicating whether the user should be able to edit values in the tree
		/// </summary>
		public bool AllowEditing {
			get => allowEditing;
			set {
				if (allowEditing != value) {
					allowEditing = value;
					ReloadData ();
				}
			}
		}

		/// <summary>
		/// Gets a value indicating whether or not the user should be able to expand nodes in the tree.
		/// </summary>
		public bool AllowExpanding { get; set; }

		/// <summary>
		/// Gets a value indicating whether the user should be able to add watch expressions to the tree
		/// </summary>
		public bool AllowWatchExpressions {
			get => allowWatchExpressions;
			set {
				if (allowWatchExpressions != value) {
					allowWatchExpressions = value;
					ReloadData ();
				}
			}
		}

		/// <summary>
		/// Gets or sets the pinned watch for the view. When a watch is pinned, the view should display only this value
		/// </summary>
		public PinnedWatch PinnedWatch {
			get => pinnedWatch;
			set {
				if (pinnedWatch != value) {
					pinnedWatch = value;
					Runtime.RunInMainThread (() => {
						if (value == null) {
							pinColumn.Width = 16;
						} else {
							pinColumn.Width = 38;
						}
					}).Ignore ();
				}
			}
		}

		/// <summary>
		/// Gets a value indicating the offset required for pinned watches
		/// </summary>
		public int PinnedWatchOffset {
			get {
				return (int) Frame.Height;
			}
		}

		void ResetColumnSizes ()
		{
			nameColumnWidth = 0.3;
			valueColumnWidth = 0.5;
			typeColumnWidth = 0.2;
		}

		void AdjustColumnSizes ()
		{
			if (Hidden || compactView)
				return;

			var width = (double) Bounds.Width;
			var available = width;
			int columnWidth;

			columnWidth = Math.Max ((int) (width * valueColumnWidth), MinimumColumnWidth);
			valueColumn.Width = columnWidth;
			available -= columnWidth;

			columnWidth = Math.Max ((int) (width * nameColumnWidth), MinimumColumnWidth);
			nameColumn.Width = columnWidth;
			available -= columnWidth;

			columnWidth = Math.Max ((int) available, MinimumColumnWidth);
			typeColumn.Width = columnWidth;
		}

		internal void CompactColumns ()
		{
			if (!compactView)
				return;

			var available = (double) Bounds.Width;

			if (pinColumn != null)
				available -= pinColumn.Width;

			var valueWidth = Math.Max (Math.Min (available / 2, valueColumn.Width), MinimumColumnWidth);

			valueColumn.Width = (nfloat) valueWidth;
			available -= valueWidth;

			nameColumn.Width = (nfloat) Math.Max (available, MinimumColumnWidth);
		}

		internal void SetCustomFont (Pango.FontDescription font)
		{
			// TODO: set fonts for all cell views
		}

		public override void ViewDidMoveToSuperview ()
		{
			base.ViewDidMoveToSuperview ();
			AdjustColumnSizes ();
			CompactColumns ();
		}

		public override void ViewDidEndLiveResize ()
		{
			base.ViewDidEndLiveResize ();
			AdjustColumnSizes ();
			CompactColumns ();
		}

		public override void ViewDidUnhide ()
		{
			base.ViewDidHide ();
			AdjustColumnSizes ();
			CompactColumns ();
		}

		/// <summary>
		/// Triggered when the view tries to expand a node. This may trigger a load of
		/// the node's children
		/// </summary>
		public event EventHandler<ObjectValueNodeEventArgs> NodeExpand;

		public void ExpandNode (ObjectValueNode node)
		{
			NodeExpand?.Invoke (this, new ObjectValueNodeEventArgs (node));
		}

		/// <summary>
		/// Triggered when the view tries to collapse a node.
		/// </summary>
		public event EventHandler<ObjectValueNodeEventArgs> NodeCollapse;

		public void CollapseNode (ObjectValueNode node)
		{
			NodeCollapse?.Invoke (this, new ObjectValueNodeEventArgs (node));
		}

		/// <summary>
		/// Triggered when the view requests a node to fetch more of it's children
		/// </summary>
		public event EventHandler<ObjectValueNodeEventArgs> NodeLoadMoreChildren;

		internal void LoadMoreChildren (ObjectValueNode node)
		{
			NodeLoadMoreChildren?.Invoke (this, new ObjectValueNodeEventArgs (node));
		}

		/// <summary>
		/// Triggered when the view needs the node to be refreshed
		/// </summary>
		public event EventHandler<ObjectValueNodeEventArgs> NodeRefresh;

		internal void Refresh (ObjectValueNode node)
		{
			NodeRefresh?.Invoke (this, new ObjectValueNodeEventArgs (node));
		}

		/// <summary>
		/// Triggered when the view needs to know if the node can be edited
		/// </summary>
		public event EventHandler<ObjectValueNodeEventArgs> NodeGetCanEdit;

		internal bool GetCanEditNode (ObjectValueNode node)
		{
			var args = new ObjectValueNodeEventArgs (node);
			NodeGetCanEdit?.Invoke (this, args);
			return args.Response is bool b && b;
		}

		/// <summary>
		/// Triggered when the node's value has been edited by the user
		/// </summary>
		public event EventHandler<ObjectValueEditEventArgs> NodeEditValue;

		internal bool GetEditValue (ObjectValueNode node, string newText)
		{
			var args = new ObjectValueEditEventArgs (node, newText);
			NodeEditValue?.Invoke (this, args);
			return args.Response is bool b && b;
		}

		/// <summary>
		/// Triggered when the user removes a node (an expression)
		/// </summary>
		public event EventHandler<ObjectValueNodeEventArgs> NodeRemoved;

		/// <summary>
		/// Triggered when the user pins the node
		/// </summary>
		public event EventHandler<ObjectValueNodeEventArgs> NodePinned;

		void CreatePinnedWatch (ObjectValueNode node)
		{
			var expression = node.Expression;

			if (string.IsNullOrEmpty (expression))
				return;

			if (PinnedWatch != null) {
				// Note: the row that the user just pinned will no longer be visible once
				// all of the root children are collapsed.
				currentHoverRow = -1;

				foreach (var child in dataSource.Root.Children) {
					if (dataSource.TryGetValue (child, out var item))
						CollapseItem (item, true);
				}
			}

			NodePinned?.Invoke (this, new ObjectValueNodeEventArgs (node));
		}

		public void Pin (ObjectValueNode node)
		{
			CreatePinnedWatch (node);
		}

		/// <summary>
		/// Triggered when the pinned watch is removed by the user
		/// </summary>
		public event EventHandler<EventArgs> NodeUnpinned;

		public void Unpin (ObjectValueNode node)
		{
			NodeUnpinned?.Invoke (this, EventArgs.Empty);
		}

		/// <summary>
		/// Triggered when the visualiser for the node should be shown
		/// </summary>
		public event EventHandler<ObjectValueNodeEventArgs> NodeShowVisualiser;

		internal bool ShowVisualizer (ObjectValueNode node)
		{
			var args = new ObjectValueNodeEventArgs (node);
			NodeShowVisualiser?.Invoke (this, args);
			return args.Response is bool b && b;
		}

		/// <summary>
		/// Triggered when an expression is added to the tree by the user
		/// </summary>
		public event EventHandler<ObjectValueExpressionEventArgs> ExpressionAdded;

		internal void OnExpressionAdded (string expression)
		{
			ExpressionAdded?.Invoke (this, new ObjectValueExpressionEventArgs (null, expression));
		}

		/// <summary>
		/// Triggered when an expression is edited by the user
		/// </summary>
		public event EventHandler<ObjectValueExpressionEventArgs> ExpressionEdited;

		internal void OnExpressionEdited (ObjectValueNode node, string expression)
		{
			ExpressionEdited?.Invoke (this, new ObjectValueExpressionEventArgs (node, expression));
		}

		/// <summary>
		/// Triggered when the user starts editing a node
		/// </summary>
		public event EventHandler StartEditing;

		internal void OnStartEditing ()
		{
			StartEditing?.Invoke (this, EventArgs.Empty);
		}

		/// <summary>
		/// Triggered when the user stops editing a node
		/// </summary>
		public new event EventHandler EndEditing;

		internal void OnEndEditing ()
		{
			EndEditing?.Invoke (this, EventArgs.Empty);
		}

		void OnEvaluationCompleted (ObjectValueNode node, ObjectValueNode[] replacementNodes)
		{
			if (disposed)
				return;

			dataSource.Replace (node, replacementNodes);
			CompactColumns ();
			Resize ();
		}

		public void LoadEvaluatedNode (ObjectValueNode node, ObjectValueNode[] replacementNodes)
		{
			OnEvaluationCompleted (node, replacementNodes);
		}

		void OnChildrenLoaded (ObjectValueNode node, int startIndex, int count)
		{
			if (disposed)
				return;

			dataSource.ReloadChildren (node);
			CompactColumns ();
			Resize ();
		}

		public void LoadNodeChildren (ObjectValueNode node, int startIndex, int count)
		{
			OnChildrenLoaded (node, startIndex, count);
		}

		public void OnNodeExpanded (ObjectValueNode node)
		{
			if (disposed)
				return;

			if (node.IsExpanded) {
				// if the node is _still_ expanded then adjust UI and scroll
				if (dataSource.TryGetValue (node, out var item)) {
					if (!IsItemExpanded (item))
						ExpandItem (item);
				}

				CompactColumns ();

				// TODO: all this scrolling kind of seems awkward
				//if (path != null)
				//	ScrollToCell (path, expCol, true, 0f, 0f);
			}
		}

		public void Reload (ObjectValueNode root)
		{
			dataSource.Root = root;
			ReloadData ();
		}

		static CGPoint ConvertPointFromEvent (NSView view, NSEvent theEvent)
		{
			var point = theEvent.LocationInWindow;

			if (view.Window != null && theEvent.WindowNumber != view.Window.WindowNumber) {
				var rect = theEvent.Window.ConvertRectToScreen (new CGRect (point, new CGSize (1, 1)));
				rect = view.Window.ConvertRectFromScreen (rect);
				point = rect.Location;
			}

			return view.ConvertPointFromView (point, null);
		}

		void UpdatePreviewIcon (nint row, PreviewButtonIcon icon)
		{
			var rowView = GetRowView (row, false);

			if (rowView != null) {
				var nameView = (MacDebuggerObjectNameView) rowView.ViewAtColumn (0);

				nameView.SetPreviewButtonIcon (icon);
			}
		}

		void UpdatePinIcon (nint row, bool hover)
		{
			if (pinColumn == null)
				return;

			var rowView = GetRowView (row, false);

			if (rowView != null) {
				var pinView = (MacDebuggerObjectPinView) rowView.ViewAtColumn (ColumnCount - 1);

				pinView.SetMouseHover (hover);
			}
		}

		void UpdateCellViewIcons (NSEvent theEvent)
		{
			var point = ConvertPointFromEvent (this, theEvent);
			var row = GetRow (point);

			if (row != currentHoverRow) {
				if (currentHoverRow != -1) {
					UpdatePreviewIcon (currentHoverRow, PreviewButtonIcon.Hidden);
					currentHoverIcon = PreviewButtonIcon.Hidden;
					UpdatePinIcon (currentHoverRow, false);
				}
				currentHoverRow = row;
			}

			if (row == -1)
				return;

			PreviewButtonIcon icon;

			if (GetColumn (point) == 0) {
				icon = PreviewButtonIcon.Hover;
			} else {
				icon = PreviewButtonIcon.RowHover;
			}

			currentHoverIcon = icon;

			if (IsRowSelected (row))
				icon = PreviewButtonIcon.Selected;

			UpdatePreviewIcon (row, icon);
			UpdatePinIcon (row, true);
		}

		public override void MouseEntered (NSEvent theEvent)
		{
			UpdateCellViewIcons (theEvent);
			base.MouseEntered (theEvent);
		}

		public override void MouseExited (NSEvent theEvent)
		{
			if (currentHoverRow != -1) {
				UpdatePreviewIcon (currentHoverRow, PreviewButtonIcon.Hidden);
				currentHoverIcon = PreviewButtonIcon.Hidden;
				currentHoverRow = -1;

				UpdatePinIcon (currentHoverRow, false);
			}

			base.MouseExited (theEvent);
		}

		public override void MouseMoved (NSEvent theEvent)
		{
			UpdateCellViewIcons (theEvent);
			base.MouseMoved (theEvent);
		}

		internal static bool ValidObjectForPreviewIcon (ObjectValueNode node)
		{
			var obj = node.GetDebuggerObjectValue ();
			if (obj == null)
				return false;

			if (obj.IsNull)
				return false;

			if (obj.IsPrimitive) {
				//obj.DisplayValue.Contains ("|") is special case to detect enum with [Flags]
				return obj.TypeName == "string" || (obj.DisplayValue != null && obj.DisplayValue.Contains ("|"));
			}

			if (string.IsNullOrEmpty (obj.TypeName))
				return false;

			return true;
		}

		void OnSelectionChanged (object sender, EventArgs e)
		{
			if (currentHoverRow == -1)
				return;

			var row = SelectedRow;

			if (SelectedRowCount == 0 || row != currentHoverRow) {
				// reset back to what the unselected icon would be
				UpdatePreviewIcon (currentHoverRow, currentHoverIcon);
				return;
			}

			UpdatePreviewIcon (currentHoverRow, PreviewButtonIcon.Selected);
		}

		public event EventHandler Resized;

		public void Resize ()
		{
			NeedsLayout = true;
			LayoutSubtreeIfNeeded ();
			Resized?.Invoke (this, EventArgs.Empty);
		}

		[CommandUpdateHandler (EditCommands.SelectAll)]
		protected void UpdateSelectAll (CommandInfo cmd)
		{
			cmd.Enabled = dataSource.Root.Children.Count > 0;
		}

		[CommandHandler (EditCommands.SelectAll)]
		protected void OnSelectAll ()
		{
			SelectAll (this);
		}

		[CommandHandler (EditCommands.Copy)]
		protected void OnCopy ()
		{
			if (SelectedRowCount == 0)
				return;

			var str = new StringBuilder ();
			var needsNewLine = false;

			var selectedRows = SelectedRows;
			foreach (var row in selectedRows) {
				var item = (MacObjectValueNode) ItemAtRow ((nint) row);

				if (needsNewLine)
					str.AppendLine ();

				needsNewLine = true;

				var value = item.Target.DisplayValue;
				var type = item.Target.TypeName;

				if (type == "string") {
					var objVal = item.Target.GetDebuggerObjectValue ();

					if (objVal != null) {
						// HACK: we need a better abstraction of the stack frame, better yet would be to not really need it in the view
						var opt = DebuggerService.Frame.GetStackFrame ().DebuggerSession.Options.EvaluationOptions.Clone ();
						opt.EllipsizeStrings = false;
						value = '"' + Mono.Debugging.Evaluation.ExpressionEvaluator.EscapeString ((string)objVal.GetRawValue (opt)) + '"';
					}
				}

				str.Append (value);
			}

			var clipboard = NSPasteboard.GeneralPasteboard;

			clipboard.SetStringForType (str.ToString (), NSPasteboard.NSPasteboardTypeString);
		}

		[CommandHandler (EditCommands.Delete)]
		[CommandHandler (EditCommands.DeleteKey)]
		protected void OnDelete ()
		{
			var nodesToDelete = new List<ObjectValueNode> ();
			var selectedRows = SelectedRows;

			foreach (var row in selectedRows) {
				var item = (MacObjectValueNode) ItemAtRow ((nint) row);

				nodesToDelete.Add (item.Target);
			}

			foreach (var node in nodesToDelete)
				NodeRemoved?.Invoke (this, new ObjectValueNodeEventArgs (node));
		}

		[CommandUpdateHandler (EditCommands.Delete)]
		[CommandUpdateHandler (EditCommands.DeleteKey)]
		protected void OnUpdateDelete (CommandInfo cinfo)
		{
			if (!AllowWatchExpressions) {
				cinfo.Visible = false;
				return;
			}

			if (SelectedRowCount == 0) {
				cinfo.Enabled = false;
				return;
			}

			var selectedRows = SelectedRows;
			foreach (var row in selectedRows) {
				var item = (MacObjectValueNode) ItemAtRow ((nint) row);

				if (!(item.Target.Parent is RootObjectValueNode)) {
					cinfo.Enabled = false;
					break;
				}
			}
		}

		[CommandHandler (DebugCommands.AddWatch)]
		protected void OnAddWatch ()
		{
			var expressions = new List<string> ();
			var selectedRows = SelectedRows;

			foreach (var row in selectedRows) {
				var item = (MacObjectValueNode) ItemAtRow ((nint) row);
				var expression = item.Target.Expression;

				if (!string.IsNullOrEmpty (expression))
					expressions.Add (expression);
			}

			foreach (var expression in expressions)
				DebuggingService.AddWatch (expression);
		}

		[CommandUpdateHandler (DebugCommands.AddWatch)]
		protected void OnUpdateAddWatch (CommandInfo cinfo)
		{
			cinfo.Enabled = SelectedRowCount > 0;
		}

		[CommandHandler (EditCommands.Rename)]
		protected void OnRename ()
		{
			var nameView = (MacDebuggerObjectNameView) GetView (0, SelectedRow, false);

			nameView.TextField.BecomeFirstResponder ();
		}

		[CommandUpdateHandler (EditCommands.Rename)]
		protected void OnUpdateRename (CommandInfo cinfo)
		{
			cinfo.Visible = AllowWatchExpressions;
			cinfo.Enabled = SelectedRowCount == 1;
		}

		protected override void Dispose (bool disposing)
		{
			if (disposing && !disposed) {
				treeViewDelegate.SelectionChanged -= OnSelectionChanged;
				treeViewDelegate.Dispose ();
				treeViewDelegate = null;
				dataSource.Dispose ();
				dataSource = null;
				disposed = true;
			}

			base.Dispose (disposing);
		}
	}
}
