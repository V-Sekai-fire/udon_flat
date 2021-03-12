using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.CodeDom;
using VRC.Udon.Graph;
using VRC.Udon.Editor;

namespace SharperUdon {
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
	static Dictionary<string, CodeExpression> operatorExprs = new Dictionary<string, CodeExpression>{
		{"op_UnaryNegation", ExprGen.opNegation},
		{"op_UnaryPlus",  new CodeSnippetExpression("+")},
		{"op_UnaryMinus", new CodeSnippetExpression("-")},
		// TODO: implement surrogate
		{"op_LeftShift",  new CodeSnippetExpression("LeftShift")},
		{"op_RightShift", new CodeSnippetExpression("RightShift")},
		{"op_LogicalXor", new CodeSnippetExpression("LogicalXor")},
	};
	static Dictionary<UdonNodeParameter.ParameterType, FieldDirection> parameterDirs = new Dictionary<UdonNodeParameter.ParameterType, FieldDirection> {
		{UdonNodeParameter.ParameterType.IN,     FieldDirection.In},
		{UdonNodeParameter.ParameterType.OUT,    FieldDirection.Out},
		{UdonNodeParameter.ParameterType.IN_OUT, FieldDirection.Ref},
	};
	public static string COPY = "Get_Variable";
	public static CodeStatement Extern(UdonNodeDefinition nodeDef, CodeExpression[] paramExprs) {
		var name = nodeDef.name.Split(' ').Last();
		var type = nodeDef.type;
		var paramDefs = nodeDef.parameters;
		var typeExpr = (CodeExpression)new CodeTypeReferenceExpression(type);
		var paramExprsSkipLast = paramExprs.Take(paramDefs.Count-1);

		var rhs = (CodeExpression)null;
		var lhs = (CodeExpression)null;
		if(nodeDef.fullName == COPY)
			(lhs, rhs) = (paramExprs[1], paramExprs[0]);
		if(rhs == null) { // operator & constructor
			if(operatorTypes.ContainsKey(name))
				rhs = new CodeBinaryOperatorExpression(paramExprs[0], operatorTypes[name], paramExprs[1]);
			else if(operatorExprs.ContainsKey(name))
				rhs = new CodeDelegateInvokeExpression(operatorExprs[name], paramExprsSkipLast.ToArray());
			else if(name == "op_Implicit")
				rhs = paramExprs[0];
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
}
}