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
	static Dictionary<string, CodeExpression> opUnaryExpr = new Dictionary<string, CodeExpression>{
		{"op_UnaryPlus", new CodeSnippetExpression("+")},
		{"op_UnaryMinus", new CodeSnippetExpression("-")},
		{"op_UnaryNegation", ExprGen.opNegation},
	};
	static Dictionary<string, CodeBinaryOperatorType> opBinaryType = new Dictionary<string, CodeBinaryOperatorType>{
		{"op_Addition", CodeBinaryOperatorType.Add},
		{"op_Subtraction", CodeBinaryOperatorType.Subtract},
		{"op_Multiply", CodeBinaryOperatorType.Multiply},
		{"op_Multiplication", CodeBinaryOperatorType.Multiply},
		{"op_Division", CodeBinaryOperatorType.Divide},
		{"op_Modulus", CodeBinaryOperatorType.Modulus},
		{"op_Remainder", CodeBinaryOperatorType.Modulus},
		
		{"op_Equality", CodeBinaryOperatorType.IdentityEquality},
		{"op_Inequality", CodeBinaryOperatorType.IdentityInequality},
		{"op_LessThan", CodeBinaryOperatorType.LessThan},
		{"op_LessThanOrEqual", CodeBinaryOperatorType.LessThanOrEqual},
		{"op_GreaterThan", CodeBinaryOperatorType.GreaterThan},
		{"op_GreaterThanOrEqual", CodeBinaryOperatorType.GreaterThanOrEqual},

		{"op_ConditionalAnd", CodeBinaryOperatorType.BooleanAnd},
		{"op_ConditionalOr", CodeBinaryOperatorType.BooleanOr},
		{"op_LogicalAnd", CodeBinaryOperatorType.BitwiseAnd},
		{"op_LogicalOr", CodeBinaryOperatorType.BitwiseOr},
		// missing: shift, xor
	};
	static Dictionary<UdonNodeParameter.ParameterType, FieldDirection> dirParameter = new Dictionary<UdonNodeParameter.ParameterType, FieldDirection> {
		{UdonNodeParameter.ParameterType.IN, FieldDirection.In},
		{UdonNodeParameter.ParameterType.OUT, FieldDirection.Out},
		{UdonNodeParameter.ParameterType.IN_OUT, FieldDirection.Ref},
	};
	public static string COPY = "Get_Variable";
	public static CodeStatement Extern(UdonNodeDefinition nodeDef, CodeExpression[] paramExprs=null) {
		var name = nodeDef.name.Split(' ').Last();
		var type = nodeDef.type;
		var typeExpr = (CodeExpression)new CodeTypeReferenceExpression(type);

		var paramDefs = nodeDef.parameters;
		if(paramExprs == null) // for testing
			paramExprs = Enumerable.Range(0, paramDefs.Count).Select(i =>
				paramDefs[i].type == typeof(System.Type) ? new CodeTypeOfExpression($"_{i}")
				: (CodeExpression) new CodeVariableReferenceExpression($"_{i}")).ToArray();

		var rhs = (CodeExpression)null;
		var lhs = (CodeExpression)null;
		if(nodeDef.fullName == COPY)
			(lhs, rhs) = (paramExprs[1], paramExprs[0]);
		if(rhs == null) { // operator & constructor
			if(opBinaryType.ContainsKey(name) && paramDefs.Count == 3)
				rhs = new CodeBinaryOperatorExpression(paramExprs[0], opBinaryType[name], paramExprs[1]);
			else if(opUnaryExpr.ContainsKey(name) && paramDefs.Count == 2)
				rhs = new CodeDelegateInvokeExpression(opUnaryExpr[name], paramExprs[0]);
			else if((name == "op_Implicit" || name == "op_Explicit") && paramDefs.Count == 2)
				rhs = new CodeCastExpression(paramDefs[1].type, paramExprs[0]);
			else if(name == "ctor")
				rhs = type.IsArray ? new CodeArrayCreateExpression(type, paramExprs[0]) : // only 1d is supported
					(CodeExpression) new CodeObjectCreateExpression(type, paramExprs.Take(paramDefs.Count-1).ToArray());
			if(rhs != null)
				lhs = paramExprs[paramDefs.Count-1];
		}
		if(rhs == null) { // getter & setter
			if(Regex.IsMatch(name, "^(get_|set_)") && (paramDefs.Count == 1 || paramDefs.Count == 2))
				rhs = new CodePropertyReferenceExpression(
					paramDefs.Count == 2 ? paramExprs[paramDefs.Count-2] : typeExpr, name.Substring(4));
			else if(Regex.IsMatch(name, type.IsArray ? "^(Get|Set)$" : "^(get_Item|set_Item)$") && paramDefs.Count >= 2) {
				var exprs = paramExprs.Take(paramDefs.Count-1);
				if(paramDefs[0].type != type)
					exprs = exprs.Prepend(typeExpr);
				rhs = type.IsArray ? new CodeArrayIndexerExpression(exprs.First(), exprs.Skip(1).ToArray()) :
					(CodeExpression) new CodeIndexerExpression(exprs.First(), exprs.Skip(1).ToArray());
			}
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
					: new CodeDirectionExpression(dirParameter[paramDefs[i].parameterType], paramExprs[i]));
				if(paramDefs[i].type.IsGenericType)
					isGeneric = true;
			}
			lhs = returnCount > 0 ? (CodeExpression)paramExprs[paramDefs.Count-1] : null;
			rhs = invoke;
		}
		return lhs == null ? (CodeStatement)new CodeExpressionStatement(rhs) : new CodeAssignStatement(lhs, rhs);
	}
}
}