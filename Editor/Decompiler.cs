using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.CodeDom;
using VRC.Udon.Common.Interfaces;
using VRC.Udon.Graph;
using VRC.Udon.Editor;

namespace SharperUdon {
public class Symbol {
	public string name;
	public uint addr;
	public System.Type type;
	public object value;

	public System.Type caster;
	public bool exported;
	public string prettyName;
	public UdonSyncInterpolationMethod? sync;
	public CodeExpression constExpr;

	public Symbol(string name, IUdonSymbolTable table, IUdonHeap heap, IUdonSyncMetadataTable syncTable, bool isConst) {
		this.name = name;
		addr = table.GetAddressFromSymbol(name);
		type = table.GetSymbolType(name);
		value = heap?.GetHeapVariable(addr);

		exported = table.HasExportedSymbol(name);

		prettyName = name;
		var m = Regex.Match(name, @"^__\d+_(.+)_\w+$");
		if(m.Success)
			prettyName = $"_{m.Groups[1].Value}_{addr}";

		if(isConst) {
			constExpr = ExprGen.Ref(value);
			var valueStr = value as string;
			if(valueStr != null && valueStr.Contains('\n'))
				constExpr = null;
		}

		if(type.IsArray && value != null)
			caster = getCaster(value);

		var syncProps = syncTable.GetSyncMetadataFromSymbol(name)?.Properties;
		if(syncProps != null)
			sync = syncProps[0].InterpolationAlgorithm;
	}
	public override string ToString() {
		return prettyName;
	}
	public CodeTypeMember FormatDefinition() {
		var field = new CodeMemberField(type, prettyName);
		field.Attributes = MemberAttributes.Final;
		if(exported)
			field.Attributes |= MemberAttributes.Public;
		else
			field.Attributes |= MemberAttributes.Private;
		if(!(value == null || (type.IsValueType && System.Activator.CreateInstance(type).Equals(value))))
			field.InitExpression = ExprGen.Value(value);
		if(sync != null) {
			field.CustomAttributes = new CodeAttributeDeclarationCollection();
			field.CustomAttributes.Add(new CodeAttributeDeclaration("UdonSharp.UdonSynced",
				new CodeAttributeArgument(new CodePropertyReferenceExpression(
					new CodeTypeReferenceExpression("UdonSharp.UdonSyncMode"),
					sync.Value.ToString()))));
		}
		return field;
	}

	static System.Type getCaster(object value) {
		var arr = (System.Array)value;
		var n = arr.GetLength(0);
		if(n == 0)
			return null;
		if(arr.GetValue(0) == null)
			return null;
		var elemType = arr.GetValue(0).GetType();
		if(!elemType.IsEnum)
			return null;
		for(int i=0; i<n; i++)
			if(!System.Enum.ToObject(elemType, i).Equals(arr.GetValue(i)))
				return null;
		return elemType;
	}
}
public class Decompiler {
	public IUdonProgram program;
	public string name = "Unnamed";

	public static UdonNodeDefinition GetNodeDefinition(string fullName) {
		return UdonEditorManager.Instance.GetNodeDefinition(fullName);
	}

	public IRGen ir;
	public CtrlFlowAnalyzer ctrlFlow;
	public DataFlowAnalyzer dataFlow;

	public Symbol[] symbols;
	public Dictionary<string, Symbol> symbolFromName;
	public void InitSymbols() {
		var heap = program.Heap;
		var symbolTable = program.SymbolTable;
		var symbolNames = symbolTable.GetSymbols().ToArray();
		var syncTable = program.SyncMetadataTable;

		symbols = new Symbol[symbolNames.Length];
		symbolFromName = new Dictionary<string, Symbol>();
		for(int i=0; i<symbolNames.Length; i++) {
			var symbol = new Symbol(symbolNames[i],
				symbolTable, heap, syncTable, !dataFlow.variables.Contains(symbolNames[i]));
			symbols[i] = symbol;
			symbolFromName[symbol.name] = symbol;
		}
	}

	public void Init() {
		ir = new IRGen{program=program};
		ir.Disassemble();
		ir.Generate();

		ctrlFlow = new CtrlFlowAnalyzer{program=program, ir=ir};
		ctrlFlow.Analyze();

		dataFlow = new DataFlowAnalyzer{program=program, ir=ir, ctrlFlow=ctrlFlow};
		dataFlow.Analyze();

		InitSymbols();
	}
	
	int getAssignLeftUsageCount(int line, CodeExpression assignLeft) {
		var assignVar = assignLeft as CodeVariableReferenceExpression;
		if(assignVar == null)
			return 2;
		var linePorts = dataFlow.ports[line];
		return linePorts[linePorts.Length-1].outputRef;
	}

	void SimplifyInParameters(int line, CodeExpression[] paramExprs) {
		// return;
		for(int i=0; i<paramExprs.Length; i++) {
			var refExpr = paramExprs[i] as CodeVariableReferenceExpression;
			if(refExpr == null)
				continue;
			var symbol = symbolFromExpr[refExpr];
			if(symbol.constExpr != null)
				paramExprs[i] = symbol.constExpr;
		}
		
		for(int prevLine=line-1; prevLine>=0 && !ctrlFlow.jumpTargets.Contains(prevLine+1); prevLine--) {
			if(statements[prevLine] != null) {
				var prevAssign = statements[prevLine] as CodeAssignStatement;
				if(prevAssign == null)
					break;

				// Debug.Log($"{line} try merge {ExprGen.GenerateCode(prevAssign)} into {string.Join(", ", paramExprs.Select(x=>$"{x}"))}");
				if(getAssignLeftUsageCount(prevLine, prevAssign.Left) > 1) {
					// Debug.Log("getAssignLeftUsageCount fail");
					break;
				}
				var idx = System.Array.IndexOf(paramExprs, prevAssign.Left);
				if(idx < 0) {
					// Debug.Log("IndexOf fail");
					break;
				}
				// Debug.Log("OK");

				paramExprs[idx] = prevAssign.Right;
				statements[prevLine] = null;
			}
		}
	}
	void SimplifyStatement(int line) {
		// return;
		var assign = statements[line] as CodeAssignStatement;
		if(assign != null) {
			var indexer = assign.Right as CodeArrayIndexerExpression;
			if(indexer != null && indexer.TargetObject is CodeVariableReferenceExpression) {
				var symbol = symbolFromExpr[(CodeVariableReferenceExpression)indexer.TargetObject];
				if(symbol.caster != null && indexer.Indices.Count == 1)
					 assign.Right = new CodeCastExpression(symbol.caster, indexer.Indices[0]);
			}
			if(getAssignLeftUsageCount(line, assign.Left) == 0) {
				statements[line] = new CodeExpressionStatement(assign.Right);
			}
		}
	}

	public Dictionary<CodeVariableReferenceExpression, Symbol> symbolFromExpr;
	public CodeStatement[] statements;
	public void Translate() {
		var symbolExprs = new Dictionary<string, CodeVariableReferenceExpression>();
		symbolFromExpr = new Dictionary<CodeVariableReferenceExpression, Symbol>();
		foreach(var symbol in symbols) {
			symbolExprs[symbol.name] = new CodeVariableReferenceExpression($"var_{symbol.name}");
			symbolFromExpr[symbolExprs[symbol.name]] = symbol;
		}

		statements = new CodeStatement[ir.irCode.Length];
		for(int line=0; line<ir.irCode.Length; line++) {
			var instr = ir.irCode[line];
			if(instr.opcode == Opcode.EXTERN) {
				if(dataFlow.port0[line].outputRef == 0)
					continue;
				var nodeDef = GetNodeDefinition(instr.arg0);
				var paramExprs = instr.args.Select((s,i) =>
					nodeDef.parameters[i].parameterType == UdonNodeParameter.ParameterType.IN ?
						(CodeExpression)symbolExprs[s] : null).ToArray();
				SimplifyInParameters(line, paramExprs);
				for(int i=0; i<paramExprs.Length; i++)
					if(paramExprs[i] == null)
						paramExprs[i] = symbolExprs[instr.args[i]];
				statements[line] = StatGen.Extern(nodeDef, paramExprs);
				// COPY may produce casting
				if(instr.arg0 == StatGen.COPY) {
					var assign = (CodeAssignStatement)statements[line];
					var sourceType = symbolFromName[instr.args[0]].type;
					var targetType = symbolFromName[instr.args[1]].type;
					if(!targetType.IsAssignableFrom(sourceType))
						assign.Right = new CodeCastExpression(targetType, assign.Right);
				}
				SimplifyStatement(line);

			} else if(instr.opcode == Opcode.CALL) {
				Debug.Assert(instr.args == null);
				statements[line] = new CodeExpressionStatement(new CodeDelegateInvokeExpression(
					new CodeVariableReferenceExpression(ctrlFlow.entries[ir.irLineFromAddr[instr.arg0]])));
			} else if(instr.opcode == Opcode.JUMP || instr.opcode == Opcode.EXIT || instr.opcode == Opcode.RETURN) {
				var paramExprs = new CodeExpression[]{new CodePrimitiveExpression(true)};
				if(instr.args != null) {
					paramExprs[0] = symbolExprs[instr.args[0]];
					SimplifyInParameters(line, paramExprs);
					paramExprs[0] = ExprGen.Negate(paramExprs[0]);
				}
				if(ctrlFlow.types[line] == CtrlFlowType.Loop) {
					statements[line] = new CodeSnippetStatement($"}} while({ExprGen.GenerateCode(paramExprs[0])});");
				} else if(ctrlFlow.types[line] == CtrlFlowType.If) {
					paramExprs[0] = ExprGen.Negate(paramExprs[0]);
					statements[line] = new CodeSnippetStatement($"if({ExprGen.GenerateCode(paramExprs[0])}) {{");
				} else if(ctrlFlow.types[line] == CtrlFlowType.Else) {
					statements[line] = new CodeSnippetStatement($"}} else {{");
				} else {
					CodeStatement stat; 
					if(ctrlFlow.types[line] == CtrlFlowType.Break)
						stat = new CodeSnippetStatement("break;");
					else if(ctrlFlow.types[line] == CtrlFlowType.Continue)
						stat = new CodeSnippetStatement("continue;");
					else if(instr.opcode == Opcode.EXIT || instr.opcode == Opcode.RETURN)
						stat = new CodeMethodReturnStatement();
					else
						stat = new CodeGotoStatement(ctrlFlow.labels[ir.irLineFromAddr[instr.arg0]]);

					var prim = paramExprs[0] as CodePrimitiveExpression;
					if(prim != null && prim.Value is bool) {
						if((bool)prim.Value)
							statements[line] = stat;
					} else {
						statements[line] = new CodeSnippetStatement($"if({ExprGen.GenerateCode(paramExprs[0])})\r\n\t{ExprGen.GenerateCode(stat).Trim()}");
					}
				}
			} else {
				Debug.LogError(instr);
			}
		}
	}

	public void GenerateCode(System.IO.TextWriter writer) {
		var usedSymbols = new HashSet<Symbol>();
		var code = new string[ir.irCode.Length];
		for(int line=0; line<ir.irCode.Length; line++) {
			code[line] = ExprGen.GenerateCode(statements[line]);
			code[line] = Regex.Replace(code[line], @"var_(\w+)", m => {
				var symbol = symbolFromName[m.Groups[1].Value];
				usedSymbols.Add(symbol);
				return symbol.prettyName;
			});
		}

		writer.WriteLine($"public class {name} : UdonSharp.UdonSharpBehaviour {{");

		var entry = default(string);
		var tab = "\t";
		foreach(var symbol in symbols) {
			if(symbol.exported || usedSymbols.Contains(symbol)) {
				writer.Write(tab);
				writer.Write(ExprGen.GenerateCode(symbol.FormatDefinition()));
			}
		}

			
		for(int line=0; line<ir.irCode.Length; line++) {
			for(int i=ctrlFlow.choiceEnd[line]; i>0; i--) {
				tab = tab.Substring(1); writer.Write(tab); writer.WriteLine("}");
			}
			if(ctrlFlow.entries[line] != null) {
				var methodExpr = new CodeMemberMethod();
				methodExpr.Name = ctrlFlow.entries[line];
				methodExpr.Attributes = MemberAttributes.Final;
				if(program.EntryPoints.HasExportedSymbol(methodExpr.Name))
					methodExpr.Attributes |= MemberAttributes.Public;
				else
					methodExpr.Attributes |= MemberAttributes.Private;

				if(dataFlow.eventFromEntry.ContainsKey(methodExpr.Name)) {
					methodExpr.Attributes &= ~MemberAttributes.Final;
					methodExpr.Attributes |= MemberAttributes.Override;
					var ev = dataFlow.eventFromEntry[methodExpr.Name];
					for(int i=0; i<ev.symbolNames.Length; i++)
						methodExpr.Parameters.Add(new CodeParameterDeclarationExpression(
							ev.nodeDef.parameters[i].type, ev.symbolNames[i]));
				}
				if(entry != null) {
					if(ctrlFlow.entryContinue[line]) {
						writer.Write(tab);
						writer.WriteLine($"{ctrlFlow.entries[line]}();");
					}
					tab = tab.Substring(1); writer.Write(tab); writer.WriteLine("}");
				}
				writer.Write(tab);
				writer.Write(ExprGen.GenerateCode(methodExpr).Split('{')[0]);
				writer.WriteLine(" {"); tab += "\t";
				entry = ctrlFlow.entries[line];
			}
			if(ctrlFlow.labels[line] != null) {
				writer.Write(tab.Substring(1));
				writer.WriteLine($"{ctrlFlow.labels[line]}:");
			}
			for(int i=ctrlFlow.loopBegin[line]; i>0; i--) {
				writer.Write(tab); writer.WriteLine("do {"); tab += "\t";
			}
			if(statements[line] != null) {
				if(ctrlFlow.types[line] == CtrlFlowType.Loop || ctrlFlow.types[line] == CtrlFlowType.Else) {
					tab = tab.Substring(1);// writer.Write(tab); writer.WriteLine("}");
				}
				foreach(var stat in code[line].Split('\n')) {
					if(string.IsNullOrEmpty(stat))
						continue;
					writer.Write(tab);
					writer.WriteLine(stat.Trim('\r', '\n'));
				}
				if(ctrlFlow.types[line] == CtrlFlowType.If || ctrlFlow.types[line] == CtrlFlowType.Else) {
					tab += "\t";
				}
			}
		}
		if(entry != null) {
			tab = tab.Substring(1); writer.Write(tab); writer.WriteLine("}");
		}
		writer.WriteLine("}");
	}
}
}