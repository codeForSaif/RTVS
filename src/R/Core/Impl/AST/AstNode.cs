﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Diagnostics;
using Microsoft.Languages.Core.Text;
using Microsoft.Languages.Core.Utility;
using Microsoft.R.Core.Parser;

namespace Microsoft.R.Core.AST {
    /// <summary>
    /// Base class of AST nodes that can have child nodes such as expressions, 
    /// loops, assignments and other statements. Complex nodes acts as composite 
    /// text ranges that encompass all tokens that constitute the node. Supports 
    /// visitor pattern that allows traversal of the subtree starting at this node. 
    /// It is a property owner and allows attaching arbitrary properties.
    /// </summary>
    [DebuggerDisplay("[Children = {Children.Count}  {Start}...{End}, Length = {Length}]")]
    public abstract class AstNode : IAstNode {
        private Lazy<PropertyDictionary> _properties = new Lazy<PropertyDictionary>(() => new PropertyDictionary());
        private IAstNode _parent;
        protected TextRangeCollection<IAstNode> _children = new TextRangeCollection<IAstNode>();

        #region IAstNode
        /// <summary>
        /// AST root node
        /// </summary>
        public virtual AstRoot Root {
            get { return Parent != null ? Parent.Root : null; }
        }

        /// <summary>
        /// This node's parent
        /// </summary>
        public IAstNode Parent {
            get { return _parent; }
            set {
                if (_parent != null && _parent != value && value != null) {
                    throw new InvalidOperationException("Node already has parent");
                }

                _parent = value;
                if (_parent != null) {
                    _parent.AppendChild(this);
                }
            }
        }

        public virtual IReadOnlyTextRangeCollection<IAstNode> Children {
            get { return _children; }
        }

        public void AppendChild(IAstNode child) {
            if (child.Parent == null) {
                child.Parent = this;
            } else if (child.Parent == this) {
#if DEBUG
                //foreach (var c in _children)
                //{
                //    Debug.Assert(!TextRange.Intersect(c, child), "Children collection already contains overlapping node");
                //}
#endif
                _children.AddSorted(child);
            } else {
                throw new InvalidOperationException("Node already has parent");
            }
        }

        public void RemoveChildren(int start, int count) {
            if (count == 0)
                return;

            if (start < 0 || start >= Children.Count)
                throw new ArgumentOutOfRangeException(nameof(start));

            if (count < 0 || count > Children.Count || start + count > Children.Count)
                throw new ArgumentOutOfRangeException(nameof(count));

            if (Children.Count == count) {
                _children = new TextRangeCollection<IAstNode>();
            } else {
                var newChildren = new IAstNode[Children.Count - count];

                int j = 0;

                for (int i = 0; i < start; i++, j++)
                    newChildren[j] = Children[i];

                for (int i = start; i < start + count; i++)
                    Children[i].Parent = null;

                for (int i = start + count; i < Children.Count; i++, j++)
                    newChildren[j] = Children[i];

                _children = new TextRangeCollection<IAstNode>(newChildren);
            }
        }

        #region Node lookup
        /// <summary>
        /// Finds deepest node that contains given position
        /// </summary>
        /// <param name="position">Position</param>
        /// <returns>Node or null if not found</returns>
        public virtual IAstNode NodeFromPosition(int position) {
            if (!this.Contains(position)) {
                return null; // not this element
            }

            for (int i = 0; i < this.Children.Count; i++) {
                var child = Children[i];

                if (child.Start > position)
                    break;

                if (child.Contains(position))
                    return child.NodeFromPosition(position);
            }

            return this;
        }

        /// <summary>
        /// Finds deepest node that fully encloses given range
        /// </summary>
        public virtual IAstNode NodeFromRange(ITextRange range, bool inclusiveEnd = false) {
            IAstNode node = null;

            if (TextRange.Contains(this, range, inclusiveEnd)) {
                node = this;

                for (int i = 0; i < this.Children.Count; i++) {
                    var child = Children[i];

                    if (range.End < child.Start)
                        break;

                    if (TextRange.Contains(child, range, inclusiveEnd)) {
                        node = (child.Children.Count > 0)
                            ? child.NodeFromRange(range, inclusiveEnd)
                            : child;

                        break;
                    }
                }
            }

            return node;
        }

        /// <summary>
        /// Determines position type and the enclosing node for 
        /// a given position in the document text.
        /// </summary>
        /// <param name="position">Position in the document text</param>
        /// <param name="node">Node that contains position</param>
        /// <returns>Position type</returns>
        public virtual PositionType GetPositionNode(int position, out IAstNode node) {
            node = null;

            if (!this.Contains(position)) {
                return PositionType.Undefined;
            }

            for (int i = 0; i < this.Children.Count; i++) {
                var child = Children[i];

                if (position < child.Start)
                    break;

                if (child.Contains(position)) {
                    return child.GetPositionNode(position, out node);
                }
            }

            node = this;
            return node is TokenNode ? PositionType.Token : PositionType.Node;
        }

        /// <summary>
        /// Finds two nodes that surround given text range
        /// </summary>
        /// <param name="start">Range start</param>
        /// <param name="length">Range length</param>
        /// <param name="startNode">Node that precedes the range or null if there is none</param>
        /// <param name="startPositionType">Type of position in the start node</param>
        /// <param name="endNode">Node that follows the range or null if there is none</param>
        /// <param name="endPositionType">Type of position in the end node</param>
        /// <returns>Node that encloses the range or root node</returns>
        public IAstNode GetElementsEnclosingRange(
                                int start, int length,
                                out IAstNode startNode, out PositionType startPositionType,
                                out IAstNode endNode, out PositionType endPositionType) {
            int end = start + length;

            startPositionType = PositionType.Undefined;
            endPositionType = PositionType.Undefined;

            startNode = null;
            endNode = null;

            startPositionType = GetPositionNode(start, out startNode);
            endPositionType = GetPositionNode(end, out endNode);

            if (startNode == endNode)
                return startNode;

            return this;
        }

        #endregion

        #endregion

        #region IPropertyOwner
        public PropertyDictionary Properties {
            get { return _properties.Value; }
        }
        #endregion

        #region IParseItem
        public virtual bool Parse(ParseContext context, IAstNode parent = null) {
            Parent = parent;
            return true;
        }
        #endregion

        #region ITextRange
        public virtual int Start {
            get { return Children.Count > 0 ? Children[0].Start : 0; }
        }

        public virtual int End {
            get { return Children.Count > 0 ? Children[Children.Count - 1].End : 0; }
        }

        public int Length {
            get { return End - Start; }
        }

        public virtual bool Contains(int position) {
            return TextRange.Contains(this, position);
        }

        public virtual void Shift(int offset) {
            Children.Shift(offset);
        }
        #endregion

        #region ICompositeTextRange
        public virtual void ShiftStartingFrom(int position, int offset) {
            Children.ShiftStartingFrom(position, offset);
        }
        #endregion

        #region IAstVisitorPattern
        public virtual bool Accept(IAstVisitor visitor, object parameter) {
            if (visitor != null && visitor.Visit(this, parameter)) {
                for (int i = 0; i < Children.Count; i++) {
                    var child = Children[i] as IAstNode;

                    if (child != null && !child.Accept(visitor, parameter))
                        return false;
                }

                return true;
            }

            return false;
        }

        public virtual bool Accept(Func<IAstNode, object, bool> visitor, object parameter) {
            if (visitor != null && visitor(this, parameter)) {
                foreach (IAstNode child in Children) {
                    if (!child.Accept(visitor, parameter))
                        return false;
                }

                return true;
            }

            return false;
        }
        #endregion
    }
}
