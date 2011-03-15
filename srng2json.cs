////////////////////////////////////////////////////////////////////
//
// srng2json - Converts RELAX NG simple syntax schemas to JSON.
//
// Copyright (C) 2010-2011 Bjoern Hoehrmann <bjoern@hoehrmann.de>
//
// This program is free software; you can redistribute it and/or
// modify it under the terms of the GNU General Public License
// as published by the Free Software Foundation; either version 2
// of the License, or (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
//
////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Collections;
using System.Xml;
using System.Xml.Linq;
using System.Text.RegularExpressions;
using System.Globalization;
using System.Xml.Serialization;
using System.IO;
using System.Runtime.Serialization;
using System.Web.Script.Serialization;
using System.Diagnostics;

namespace srng2json {

  class Grammar {

    public enum PatternType {
      NOT_ALLOWED,
      EMPTY,
      REF,
      ONE_OR_MORE,
      CHOICE,
      GROUP,
      TEXT,
      ATTRIBUTE,
      INTERLEAVE,
      ELEMENT,
      DEFINE,
      AND,
      NOT,
      ANYNAME,
      LNNAME,
      NSNAME,
    };

    public class Pattern {
      public Pattern p1;
      public Pattern p2;
      public bool Nullable;
      public PatternType Type;
      public string Name;
      public string Namespace;
      public int? Id;

      public override int GetHashCode() {
        if (!Id.HasValue)
          this.Id =
            (p1 != null ? p1.GetHashCode() : 0) ^
            (p2 != null ? p2.GetHashCode() : 0) ^
            (Nullable ? 1 : 0) ^
            (Name != null ? Name.GetHashCode() : 0) ^
            (Namespace != null ? Namespace.GetHashCode() : 0) ^
            Type.GetHashCode();

        return Id.Value;
      }

      public override bool Equals(object obj) {
        var other = obj as Pattern;

        if (other == null)
          return false;

        return other.p1 == this.p1 &&
          other.p2 == this.p2 &&
          other.Nullable == this.Nullable &&
          other.Name == this.Name &&
          other.Namespace == this.Namespace &&
          other.Type == this.Type;
      }

      public IEnumerable<Pattern> DescendantNodesAndSelf() {
        if (p1 != null)
          foreach (var p in p1.DescendantNodesAndSelf())
            yield return p;

        if (p2 != null)
          foreach (var p in p2.DescendantNodesAndSelf())
            yield return p;

        yield return this;
      }

      public IEnumerable<Pattern> Nodes() {
        if (p1 != null)
            yield return p1;

        if (p2 != null)
            yield return p2;
      }

    }

    Pattern Create(Pattern p) {
      if (cache.ContainsKey(p)) {
        return cache[p];
      }
      cache.Add(p, p);
      return p;
    }

    Pattern Empty() {
      return Create(new Pattern() {
        Nullable = true,
        Type = PatternType.EMPTY
      });
    }

    Pattern NotAllowed() {
      return Create(new Pattern() {
        Nullable = false,
        Type = PatternType.NOT_ALLOWED
      });
    }

    Pattern Choice(Pattern p1, Pattern p2) {

      if (p1.Type == PatternType.NOT_ALLOWED)
        return p2;

      if (p2.Type == PatternType.NOT_ALLOWED)
        return p1;

      if (p1.Type == PatternType.CHOICE)
        return Choice(p1.p1, Choice(p1.p2, p2));

      for (var child = p2; true; child = child.p2) {
        if (child == p1)
          return p2;
        if (!(child.Type == PatternType.CHOICE))
          break;
        if (child.p1 == p1)
          return p2;
      }

      return Create(new Pattern() {
        p1 = p1,
        p2 = p2,
        Nullable = p1.Nullable || p2.Nullable,
        Type = PatternType.CHOICE
      });
    }

    Pattern And(Pattern p1, Pattern p2) {

      if (p1.Type == PatternType.NOT_ALLOWED)
        return p1;

      if (p2.Type == PatternType.NOT_ALLOWED)
        return p2;

      if (p1.Type == PatternType.AND)
        return And(p1.p1, And(p1.p2, p2));

      return Create(new Pattern() {
        p1 = p1,
        p2 = p2,
        Nullable = p1.Nullable && p2.Nullable,
        Type = PatternType.AND
      });
    }

    Pattern Not(Pattern p) {
      return Create(new Pattern() {
        p1 = p,
        Nullable = !p.Nullable,
        Type = PatternType.NOT
      });
    }

    Pattern Interleave(Pattern p1, Pattern p2) {

      if (p1.Type == PatternType.NOT_ALLOWED)
        return p1;

      if (p2.Type == PatternType.NOT_ALLOWED)
        return p2;

      if (p1.Type == PatternType.EMPTY)
        return p2;

      if (p2.Type == PatternType.EMPTY)
        return p1;

      if (p1.Type == PatternType.INTERLEAVE)
        return Group(p1.p1, Group(p1.p2, p2));

      return Create(new Pattern() {
        p1 = p1,
        p2 = p2,
        Nullable = p1.Nullable && p2.Nullable,
        Type = PatternType.INTERLEAVE
      });
    }

    Pattern Group(Pattern p1, Pattern p2) {

      if (p1.Type == PatternType.NOT_ALLOWED)
        return p1;

      if (p2.Type == PatternType.NOT_ALLOWED)
        return p2;

      if (p1.Type == PatternType.EMPTY)
        return p2;

      if (p2.Type == PatternType.EMPTY)
        return p1;

      if (p1.Type == PatternType.GROUP)
        return Group(p1.p1, Group(p1.p2, p2));

      return Create(new Pattern() {
        p1 = p1,
        p2 = p2,
        Nullable = p1.Nullable && p2.Nullable,
        Type = PatternType.GROUP
      });
    }

    Pattern Optional(Pattern p) {
      return Choice(Empty(), p);
    }

    Pattern OneOrMore(Pattern p) {
      return Create(new Pattern() {
        p1 = p,
        Nullable = p.Nullable,
        Type = PatternType.ONE_OR_MORE
      });
    }

    Pattern Text() {
      return Create(new Pattern() {
        Nullable = true,
        Type = PatternType.TEXT
      });
    }

    Pattern Ref(string name) {
      return Create(new Pattern() {
        Name = name,
        Nullable = false,
        Type = PatternType.REF
      });
    }

    Pattern Attribute(string name, string ns) {
      return Create(new Pattern() {
        Name = name,
        Namespace = ns,
        Nullable = false,
        Type = PatternType.ATTRIBUTE
      });
    }

    Pattern Element(Pattern nc, Pattern p) {
      return Create(new Pattern() {
        p1 = nc,
        p2 = p,
        Nullable = p.Nullable,
        Type = PatternType.ELEMENT
      });
    }

    Pattern Define(string name, Pattern p) {
      return Create(new Pattern() {
        p1 = p,
        Name = name,
        Nullable = p.Nullable,
        Type = PatternType.DEFINE
      });
    }

    Pattern AnyName() {
      return Create(new Pattern() {
        Nullable = true,
        Type = PatternType.ANYNAME
      });
    }

    Pattern NsName(string ns) {
      return Create(new Pattern() {
        Namespace = ns,
        Nullable = false,
        Type = PatternType.NSNAME
      });
    }

    Pattern LnName(string name) {
      return Create(new Pattern() {
        Name = name,
        Nullable = false,
        Type = PatternType.LNNAME
      });
    }

    Pattern Name(string ns, string name) {
      return Group(NsName(ns), LnName(name));
    }

    string Out;
    public Grammar(string srngpath, string outpath) {
      Out = outpath;
      XDocument doc = XDocument.Load(srngpath);
      XNamespace rng = "http://relaxng.org/ns/structure/1.0";
      NameTable = doc.Root
        .Descendants(rng + "define")
        .ToDictionary(x => x.Attribute("name").Value,
          x => FromXml(x.Elements().First()));
    }

    Pattern FromXml(XElement node) {
      XNamespace rng = "http://relaxng.org/ns/structure/1.0";

      if (node.Name.Namespace != rng)
        throw new Exception();

      var children = node.Elements().Select(x => FromXml(x));

      switch (node.Name.LocalName) {
        case "anyName":
          return children.Count() > 0 ?
            And(AnyName(), children.First()) : AnyName();
        case "nsName":
          return children.Count() > 0 ?
            And(NsName(node.Attribute("ns").Value), children.First()) :
            NsName(node.Attribute("ns").Value);
        case "name":
          return Name(node.Attribute("ns").Value, node.Value);
        case "except":
          return Not(children.First());
        case "element":
          return Element(children.First(), children.Skip(1).First());
        case "define":
        case "group":
          return children.Aggregate(Group);
        case "oneOrMore":
          return OneOrMore(children.Aggregate(Group));
        case "choice":
          return children.Aggregate(Choice);
        case "interleave":
          return children.Aggregate(Interleave);
        case "zeroOrMore":
          return Optional(OneOrMore(children.Aggregate(Group)));
        case "optional":
          return Optional(children.Aggregate(Group));
        case "value":
        case "text":
        case "data":
        case "list":
          return Text();
        case "ref":
          return Ref(node.Attribute("name").Value);
        case "empty":
          return Empty();
        case "notAllowed":
          return NotAllowed();
        case "attribute":
          if (node.Elements().First().Name.LocalName != "name")
            throw new Exception("Only <name> is allowed as name class for Attributes");
          return Attribute(node.Elements().First().Value,
            node.Elements().First().Attribute("ns").Value);

        default:
          throw new Exception();
      }
    }

    Pattern Deriv(Pattern p, Pattern c) {
      switch (p.Type) {
        case PatternType.NOT_ALLOWED:
          return p;
        case PatternType.EMPTY:
          return NotAllowed();
        case PatternType.REF:
          return c.Type == PatternType.REF &&
            p.Name == c.Name ? Empty() : NotAllowed();
        case PatternType.ONE_OR_MORE:
          return Group(Deriv(p.p1, c), Optional(p));
        case PatternType.CHOICE:
          return Choice(Deriv(p.p1, c), Deriv(p.p2, c));
        case PatternType.AND:
          return And(Deriv(p.p1, c), Deriv(p.p2, c));
        case PatternType.NOT:
          return Not(Deriv(p.p1, c));
        case PatternType.GROUP:

          if (c.Type == PatternType.ATTRIBUTE)
            return Choice(Group(Deriv(p.p1, c), p.p2),
              Group(p.p1, Deriv(p.p2, c)));

          if (p.p1.Nullable)
            return Choice(Deriv(p.p2, c), Group(Deriv(p.p1, c), p.p2));
          return Group(Deriv(p.p1, c), p.p2);

        case PatternType.TEXT:
          return NotAllowed();
        case PatternType.ATTRIBUTE:
          return c.Type == PatternType.ATTRIBUTE &&
            p.Name == c.Name ? Optional(p) : NotAllowed();
        case PatternType.INTERLEAVE:
          return Choice(Interleave(Deriv(p.p1, c), p.p2),
            Interleave(p.p1, Deriv(p.p2, c)));
        case PatternType.DEFINE:
          return Define(p.Name, Deriv(p.p1, c));

        case PatternType.ANYNAME:
          return p;
        case PatternType.LNNAME:
          return c.Type == PatternType.LNNAME &&
            c.Name == p.Name ? Empty() : NotAllowed();
        case PatternType.NSNAME:
          return c.Type == PatternType.NSNAME &&
            c.Namespace == p.Namespace ? Empty() : NotAllowed();

        default:
          throw new Exception();
      }
    }

    XElement SerializePattern(Pattern p) {
      var elem = new XElement(Enum.GetName(typeof(PatternType), p.Type),
        p.Nodes().Select(x => SerializePattern(x)));
      elem.Add(new XAttribute("name", p.Name + ""),
               new XAttribute("ns", p.Namespace + ""),
               new XAttribute("nullable", p.Nullable));
      return elem;
    }

    public Dictionary<string, Pattern> NameTable = new Dictionary<string, Pattern>();
    Dictionary<Pattern, Pattern> cache = new Dictionary<Pattern, Pattern>(32768);
    Dictionary<Pattern, State> Pattern2State = new Dictionary<Pattern, State>();
    HashSet<Pattern> Seen = new HashSet<Pattern>();

    public class State {
      public Dictionary<string, State> AttrStates = new Dictionary<string, State>();
      public Dictionary<string, State> ChildStates = new Dictionary<string, State>();
      public HashSet<string> NullableDefines = new HashSet<string>();
      public bool IsNullable;
    }

    public State Simulate(Pattern elem) {

      if (Seen.Contains(elem))
        return Pattern2State[elem];

      var ElemState = new State() { IsNullable = elem.Nullable };
      var root = elem;

      if (!Pattern2State.ContainsKey(root)) {
        Pattern2State.Add(root, ElemState);
      }

      Queue<Pattern> queue = new Queue<Pattern>();
      queue.Enqueue(root);

      var leaves = elem.DescendantNodesAndSelf().Where(x =>
        x.Type == PatternType.ATTRIBUTE ||
        x.Type == PatternType.REF).Distinct().ToList();

      while (queue.Count > 0) {
        var current = queue.Dequeue();
        var cstate = Pattern2State[current];

        if (Seen.Contains(current))
          continue;

        current.DescendantNodesAndSelf()
          .Where(x => x.Type == PatternType.DEFINE)
          .Where(x => x.Nullable)
          .ToList()
          .ForEach(x => cstate.NullableDefines.Add(x.Name));

        Seen.Add(current);

        foreach (var leaf in leaves) {

          var derived = Deriv(current, leaf);

          if (derived.Type == PatternType.NOT_ALLOWED)
            continue;

          if (!Pattern2State.ContainsKey(derived)) {
            Pattern2State.Add(derived, new State() { IsNullable = derived.Nullable });
            queue.Enqueue(derived);
          }

          var dstate = Pattern2State[derived];

          if (leaf.Type == PatternType.ATTRIBUTE) {
            if (leaf.Namespace.Length > 0)
              cstate.AttrStates.Add("{" + leaf.Namespace + "}" + leaf.Name, dstate);
            else
              cstate.AttrStates.Add(leaf.Name, dstate);
          }
          else if (leaf.Type == PatternType.REF) {
            cstate.ChildStates.Add(leaf.Name, dstate);
          }
        }
      }
      if (!Pattern2State.ContainsValue(ElemState))
        throw new Exception();
      return ElemState;
    }

    public void SimulateAll() {
      var s2i = new Dictionary<State, int>();
      var names = new Dictionary<string, Dictionary<string, State>>();

      var allNCPatterns = NameTable
        .Values
        .SelectMany(x => x.p1.DescendantNodesAndSelf())
        .ToList();
      var allNamespaces = allNCPatterns
        .Where(x => x.Type == PatternType.NSNAME)
        .Distinct();
      var allLocalNames = allNCPatterns
        .Where(x => x.Type == PatternType.LNNAME)
        .Distinct();

      foreach (var ns in allNamespaces) {
        names.Add(ns.Namespace, new Dictionary<string, State>());

        foreach (var ln in allLocalNames) {

          var all = NameTable
            .Where(x => Deriv(Deriv(x.Value.p1, ns), ln).Nullable)
            .Select(x => Define(x.Key, x.Value.p2))
            .ToList();

          if (all.Count == 0)
            continue;

          var state = Simulate(all.Aggregate(Choice));

          names[ns.Namespace].Add(ln.Name, state);
        }
      }

      var nix = 1;
      var map = Pattern2State.Values.Distinct().ToDictionary(x => x, x => nix++);

      var names2 = names.ToDictionary(x => x.Key,
        x => x.Value.ToDictionary(y => y.Key, y => map[y.Value]));

      var defnull = NameTable.ToDictionary(x => x.Key, x => new HashSet<int>());

      foreach (var entry in Pattern2State.Values.Distinct()) {
        entry.NullableDefines.ToList().ForEach(x => defnull[x].Add(map[entry]));
      }

      Func<Dictionary<string,State>, Dictionary<string,int>> helper =
        delegate(Dictionary<string,State> dict) {
        var nd = new Dictionary<string,int>();
        foreach (var entry in dict) {
          defnull[entry.Key].ToList().ForEach(x => nd.Add(x.ToString(), map[entry.Value]));
        }
        return nd;
      };

      var allstates = Pattern2State.Values
        .Distinct()
        .ToDictionary(x => map[x], x => new {
        Attributes = x.AttrStates.ToDictionary(y => y.Key, y => map[y.Value]),
        IsNullable = x.IsNullable,
        ChildElems = helper(x.ChildStates),
      });

      var states = new object[allstates.Count + 1];
      allstates.ToList().ForEach(x => states[x.Key] = x.Value);

      var serializer = new JavaScriptSerializer();
      var text = serializer.Serialize(new {
        // DefNull = defnull,
        NameMap = names2,
        States = states });
      TextWriter tw = new StreamWriter(Out);
      tw.Write(text);
      tw.Close();
    }
  }

  class Program {
    static void Main(string[] args) {

      var dict = args
        .Where(x => x.StartsWith("--"))
        .Where(x => x.Contains('='))
        .ToDictionary(x => x.Substring(2, x.IndexOf('=') - 2),
          x => x.Substring(x.IndexOf('=') + 1));

      if (args.Length != 2 || dict.Count != 2 || !dict.ContainsKey("srng") || !dict.ContainsKey("out")) {
        var self = Path.GetFileName(Process.GetCurrentProcess().MainModule.FileName);
        Console.Error.WriteLine("-------------------------------------------------------------------------------");
        Console.Error.WriteLine("{0} turns RELAX NG schemas in the simple syntax into JSON. The simple", self);
        Console.Error.WriteLine("syntax is defined in <http://www.relaxng.org/spec-20011203.html#simple-syntax>.");
        Console.Error.WriteLine("Tools like http://www.kohsuke.org/relaxng/rng2srng/ convert from full syntax.");
        Console.Error.WriteLine("Note: data types and lists and mandatory or forbidden text, all not supported.");
        Console.Error.WriteLine("-------------------------------------------------------------------------------");
        Console.Error.WriteLine("  Usage: {0} --srng=path --out=path", self);
        Console.Error.WriteLine("-------------------------------------------------------------------------------");
        Console.Error.WriteLine("data:,404%20Not%20Found. - (c) 2010-2011 Bjoern Hoehrmann - bjoern@hoehrmann.de");
        Console.Error.WriteLine("-------------------------------------------------------------------------------");
        return;
      }

      var g = new Grammar(dict["srng"], dict["out"]);
      g.SimulateAll();
    }
  }
}
