using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.CodeDom;
using VRC.Udon.Graph;
using VRC.Udon.Editor;

namespace UdonFlat {
public class StatGen {
	static Dictionary<string, CodeBinaryOperatorType> operatorTypes = new Dictionary<string, CodeBinaryOperatorType>{
		{"op_Addition",       CodeBinaryOperatorType.Add},
		{"op_Subtraction",    CodeBinaryOperatorType.Subtract},
		{"op_Multiply",       CodeBinaryOperatorType.Multiply},
		{"op_Multiplication", CodeBinaryOperatorType.Multiply},
		{"op_Division",       CodeBinaryOperatorType.Divide},
		{"op_Modulus",        CodeBinaryOperatorType.Modulus},
		{"op_Remainder",      CodeBinaryOperatorType.Modulus},

		{"op_Equality",           CodeBinaryOperatorType.IdentityEquality},
		{"op_Inequality",         CodeBinaryOperatorType.IdentityInequality},
		{"op_LessThan",           CodeBinaryOperatorType.LessThan},
		{"op_GreaterThan",        CodeBinaryOperatorType.GreaterThan},
		{"op_LessThanOrEqual",    CodeBinaryOperatorType.LessThanOrEqual},
		{"op_GreaterThanOrEqual", CodeBinaryOperatorType.GreaterThanOrEqual},

		{"op_ConditionalAnd", CodeBinaryOperatorType.BooleanAnd},
		{"op_ConditionalOr",  CodeBinaryOperatorType.BooleanOr},
		{"op_LogicalAnd",     CodeBinaryOperatorType.BitwiseAnd},
		{"op_LogicalOr",      CodeBinaryOperatorType.BitwiseOr},
	};
	static Dictionary<string, (string, CodeBinaryOperatorType)> operatorStrTypes = new Dictionary<string, (string, CodeBinaryOperatorType)> {
		{"op_LeftShift",  ("<<", CodeBinaryOperatorType.LessThan)},
		{"op_RightShift", (">>", CodeBinaryOperatorType.GreaterThan)},
		{"op_LogicalXor", ("^",  CodeBinaryOperatorType.BitwiseOr)},
	};
	static Dictionary<string, CodeExpression> operatorExprs = new Dictionary<string, CodeExpression>{
		{"op_LogicalNot",    ExprGen.op_LogicalNot},
		{"op_UnaryPlus",     new CodeVariableReferenceExpression("+")},
		{"op_UnaryMinus",    new CodeVariableReferenceExpression("-")},
		{"op_UnaryNegation", new CodeVariableReferenceExpression("-")},
	};
	static Dictionary<UdonNodeParameter.ParameterType, FieldDirection> parameterDirs = new Dictionary<UdonNodeParameter.ParameterType, FieldDirection> {
		{UdonNodeParameter.ParameterType.IN,     FieldDirection.In},
		{UdonNodeParameter.ParameterType.OUT,    FieldDirection.Out},
		{UdonNodeParameter.ParameterType.IN_OUT, FieldDirection.Ref},
	};
	public static string COPY = "Get_Variable";
	public static string PatchOperator(string code) {
		return Regex.Replace(code, $@"[<>|](\s*)({string.Join("|", operatorStrTypes.Keys)})(?=\()",
			m => operatorStrTypes[m.Groups[2].Value].Item1 + m.Groups[1].Value);
	}
	public static CodeStatement Extern(UdonNodeDefinition nodeDef, CodeExpression[] paramExprs) {
		var type = nodeDef.type;
		var name = nodeDef.name.Split(' ').Last();
		var paramDefs = nodeDef.parameters;
		var paramExprsSkipLast = paramExprs.Take(paramDefs.Count-1);
		var typeExpr = (CodeExpression)new CodeTypeReferenceExpression(type);
		if(name == "op_UnaryNegation" && type == typeof(bool))
			name = "op_LogicalNot";

		var rhs = (CodeExpression)null;
		var lhs = (CodeExpression)null;
		if(nodeDef.fullName == COPY)
			(lhs, rhs) = (paramExprs[1], paramExprs[0]);
		if(rhs == null) { // operator & constructor
			if(operatorTypes.TryGetValue(name, out var opType))
				rhs = new CodeBinaryOperatorExpression(paramExprs[0], opType, paramExprs[1]);
			else if(operatorStrTypes.TryGetValue(name, out var opStrType))
				// express (A op B) as (A op' op_Name(B))
				rhs = new CodeBinaryOperatorExpression(paramExprs[0], opStrType.Item2,
					new CodeDelegateInvokeExpression(new CodeVariableReferenceExpression(name), paramExprs[1]));
			else if(operatorExprs.TryGetValue(name, out var opExpr))
				rhs = new CodeDelegateInvokeExpression(opExpr, paramExprsSkipLast.ToArray());
			else if(name == "op_Implicit")
				rhs = paramExprs[0]; // TODO: will this cause problem?
			else if(name == "op_Explicit")
				rhs = new CodeCastExpression(paramDefs[1].type, paramExprs[0]);
			else if(name == "ctor")
				rhs = type.IsArray ? new CodeArrayCreateExpression(type, paramExprs[0]) : // only 1d is supported
					(CodeExpression) new CodeObjectCreateExpression(type, paramExprsSkipLast.ToArray());
			if(rhs != null)
				lhs = paramExprs[paramDefs.Count-1];
		}
		if(rhs == null) { // getter & setter
			if(Regex.IsMatch(name, type.IsArray ? "^(Get|Set)$" : "^(get_Item|set_Item)$")) {
				var exprs = paramDefs[0].type == type ? paramExprsSkipLast : paramExprsSkipLast.Prepend(typeExpr);
				rhs = type.IsArray ? new CodeArrayIndexerExpression(exprs.First(), exprs.Skip(1).ToArray()) :
					(CodeExpression) new CodeIndexerExpression(exprs.First(), exprs.Skip(1).ToArray());
			} else if(Regex.IsMatch(name, "^(get_|set_)"))
				rhs = new CodePropertyReferenceExpression(
					paramDefs.Count == 2 ? paramExprs[paramDefs.Count-2] : typeExpr, name.Substring(4));
			if(rhs != null) {
				lhs = paramExprs[paramDefs.Count-1];
				if(Regex.IsMatch(name, "^set", RegexOptions.IgnoreCase))
					(lhs, rhs) = (rhs, lhs);
			}
		}
		if(rhs == null) { // method
			if(Regex.IsMatch(name, "^(get_|set_|op_|(ctor$))"))
				Debug.LogWarning($"not implemented: {nodeDef.fullName}");

			var instCount = 0;
			var returnCount = 0;
			var isGeneric = false;
			if(paramDefs.Count > 0) {
				var first = paramDefs[0];
				var last = paramDefs[paramDefs.Count-1];
				if(first.parameterType == UdonNodeParameter.ParameterType.IN)
					if(first.name == "instance")
						instCount++;
				if(last.parameterType == UdonNodeParameter.ParameterType.OUT)
					if(last.name == null || last.name == "T" || last.name == "T[]") {
						returnCount++;
						isGeneric = last.name != null;
					}
			}
			var invoke = new CodeMethodInvokeExpression(instCount > 0 ? paramExprs[0] : typeExpr, name);
			for(int i=instCount; i<paramDefs.Count-returnCount; i++) {
				if(isGeneric && paramDefs[i].type == typeof(System.Type)) {
					if(paramExprs[i] is CodeTypeOfExpression)
						invoke.Method.TypeArguments.Add((paramExprs[i] as CodeTypeOfExpression).Type);
					else
						Debug.LogWarning($"missing concrete type argument: {paramExprs[i]}");
					continue;
				}
				invoke.Parameters.Add(paramDefs[i].parameterType == UdonNodeParameter.ParameterType.IN ? paramExprs[i]
					: new CodeDirectionExpression(parameterDirs[paramDefs[i].parameterType], paramExprs[i]));
				if(paramDefs[i].type.IsGenericType)
					isGeneric = true;
			}
			lhs = returnCount > 0 ? (CodeExpression)paramExprs[paramDefs.Count-1] : null;
			rhs = invoke;
		}
		return lhs == null ? (CodeStatement)new CodeExpressionStatement(rhs) : new CodeAssignStatement(lhs, rhs);
	}
	public static CodeStatement Extern(UdonNodeDefinition nodeDef) {
		var paramDefs = nodeDef.parameters;
		return Extern(nodeDef, Enumerable.Range(0, paramDefs.Count).Select(i =>
				paramDefs[i].type == typeof(System.Type) ? new CodeTypeOfExpression($"_{i}")
				: (CodeExpression) new CodeVariableReferenceExpression($"_{i}")).ToArray());
	}
	public static CodeStatement If(CodeExpression condition, CodeStatement stat) {
		var prim = condition as CodePrimitiveExpression;
		if(prim != null && prim.Value is bool)
			return (bool)prim.Value ? stat : null;
		else
			return new CodeConditionStatement(condition, stat);
	}
	public static CodeStatement Break = new CodeExpressionStatement(new CodeSnippetExpression("break"));
	public static CodeStatement Continue = new CodeExpressionStatement(new CodeSnippetExpression("continue"));
	static CodeStatement noop = new CodeExpressionStatement(new CodeSnippetExpression(""));
	public static CodeIterationStatement While() {
		return new CodeIterationStatement(noop, ExprGen.True, noop);
	}
}
}