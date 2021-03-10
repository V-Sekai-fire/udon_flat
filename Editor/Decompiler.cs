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

	public bool exported;
	public string prettyName;

	public Symbol(string name, IUdonSymbolTable table, IUdonHeap heap) {
		this.name = name;
		addr = table.GetAddressFromSymbol(name);
		type = table.GetSymbolType(name);
		value = heap?.GetHeapVariable(addr);

		exported = table.HasExportedSymbol(name);

		prettyName = name;
		var m = Regex.Match(name, @"^__\d+_(.+)_\w+$");
		if(m.Success)
			prettyName = $"_{m.Groups[1].Value}_{addr}";
	}
	public override string ToString() {
		return prettyName;
	}
	public CodeTypeMember FormatDefinition() {
		var field = new CodeMemberField(type, prettyName);
		if(exported)
			field.Attributes = MemberAttributes.Public;
		if(!(value == null || (type.IsValueType && System.Activator.CreateInstance(type).Equals(value))))
			field.InitExpression = ExprGen.Value(value);
		return field;
	}
}
public class Decompiler {
	public IUdonProgram program;
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

		symbols = new Symbol[symbolNames.Length];
		symbolFromName = new Dictionary<string, Symbol>();
		for(int i=0; i<symbolNames.Length; i++) {
			var symbol = new Symbol(symbolNames[i], symbolTable, heap);
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
		var symbol = symbolFromExpr[assignVar];
		if(dataFlow.volatiles.Contains(symbol.name))
			return 2;
		return dataFlow.writeUseCount[line];
	}

	void SimplifyInParameters(int line, CodeExpression[] paramExprs) {
		for(int i=0; i<paramExprs.Length; i++) {
			var refExpr = paramExprs[i] as CodeVariableReferenceExpression;
			if(refExpr == null)
				continue;
			var symbol = symbolFromExpr[refExpr];
			if(!dataFlow.variables.Contains(symbol.name) && !symbol.type.IsArray) {
				paramExprs[i] = ExprGen.Value(symbol.value);
			}
		}
		for(int prevLine=line-1; prevLine>=0; prevLine--) {
			if(statements[prevLine] == null)
				continue;
			var prevAssign = statements[prevLine] as CodeAssignStatement;
			if(prevAssign == null)
				break;
			if(getAssignLeftUsageCount(prevLine, prevAssign.Left) > 1)
				break;
			var idx = System.Array.IndexOf(paramExprs, prevAssign.Left);
			if(idx < 0)
				break;
			

			paramExprs[idx] = prevAssign.Right;
			statements[prevLine] = null;
			if(ctrlFlow.entries[prevLine] != null || ctrlFlow.labels[prevLine] != null)
				break;
		}
	}
	void SimplifyStatement(int line) {
		var assign = statements[line] as CodeAssignStatement;
		if(assign != null && getAssignLeftUsageCount(line, assign.Left) == 0) {
			if(assign.Right is CodeVariableReferenceExpression || assign.Right is CodePrimitiveExpression)
					statements[line] = null;
				else
					statements[line] = new CodeExpressionStatement(assign.Right);
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
				var nodeDef = GetNodeDefinition(instr.arg0);
				var paramExprs = instr.args.Select((s,i) =>
					nodeDef.parameters[i].parameterType == UdonNodeParameter.ParameterType.IN ?
						(CodeExpression)symbolExprs[s] : null).ToArray();
				SimplifyInParameters(line, paramExprs);
				for(int i=0; i<paramExprs.Length; i++)
					if(paramExprs[i] == null)
						paramExprs[i] = symbolExprs[instr.args[i]];
				statements[line] = ExprGen.Extern(nodeDef, paramExprs);
				SimplifyStatement(line);

			} else if(instr.opcode == Opcode.CALL) {
				Debug.Assert(instr.args == null);
				statements[line] = new CodeExpressionStatement(new CodeDelegateInvokeExpression(
					new CodeVariableReferenceExpression(ctrlFlow.entries[ir.irLineFromAddr[instr.arg0]])));
			} else if(instr.opcode == Opcode.RETURN) {
				Debug.Assert(instr.args == null);
				statements[line] = new CodeMethodReturnStatement();
			} else if(instr.opcode == Opcode.JUMP) {
				var paramExprs = new CodeExpression[]{new CodePrimitiveExpression(true)};
				if(instr.args != null) {
					paramExprs[0] = symbolExprs[instr.args[0]];
					SimplifyInParameters(line, paramExprs);
					if(instr.args[1] == "FALSE")
						paramExprs[0] = ExprGen.Negate(paramExprs[0]);
				}
				// Debug.Log($"paramExprs[0]={paramExprs[0]}");
				if(ctrlFlow.types[line] == CtrlFlowType.Loop) {
					statements[line] = new CodeSnippetStatement($"}} while({ExprGen.GenerateCode(paramExprs[0])});");
				}
				else if(ctrlFlow.types[line] == CtrlFlowType.If) {
					paramExprs[0] = ExprGen.Negate(paramExprs[0]);
					statements[line] = new CodeSnippetStatement($"if({ExprGen.GenerateCode(paramExprs[0])}) {{");
				}
				else if(ctrlFlow.types[line] == CtrlFlowType.Else) {
					statements[line] = new CodeSnippetStatement($"}} else {{");
				}
				else {
					CodeStatement stat; 
					if(ctrlFlow.types[line] == CtrlFlowType.Break)
						stat = new CodeSnippetStatement("break;");
					else if(ctrlFlow.types[line] == CtrlFlowType.Continue)
						stat = new CodeSnippetStatement("continue;");
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
			} else if(instr.opcode == Opcode.EXIT) {
				var paramExprs = new CodeExpression[]{new CodePrimitiveExpression(true)};
				if(instr.args != null) {
					paramExprs[0] = symbolExprs[instr.args[0]];
					SimplifyInParameters(line, paramExprs);
					if(instr.args[1] == "FALSE")
						paramExprs[0] = ExprGen.Negate(paramExprs[0]);
				}
				var stat = new CodeMethodReturnStatement();
				var prim = paramExprs[0] as CodePrimitiveExpression;
				if(prim != null && prim.Value is bool) {
					if((bool)prim.Value)
						statements[line] = stat;
				} else {
					statements[line] = new CodeSnippetStatement($"if({ExprGen.GenerateCode(paramExprs[0])})\r\n\t{ExprGen.GenerateCode(stat).Trim()}");
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

		writer.WriteLine("using UnityEngine;");
		writer.WriteLine("using UdonSharp;");
		writer.WriteLine("public class Test : UdonSharpBehaviour {");

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
				if(entry != null) {
					tab = tab.Substring(1); writer.Write(tab); writer.WriteLine("}");
				}
				writer.Write(tab);
				writer.Write($"void {ctrlFlow.entries[line]}()");
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
					writer.Write(stat);
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