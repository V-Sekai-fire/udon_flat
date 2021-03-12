using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.Linq;
using System.CodeDom;
using System.CodeDom.Compiler;
using VRC.Udon;
using VRC.Udon.Common;

namespace SharperUdon {
public class ExprGen {
	public static CodeExpression opNegation = new CodeSnippetExpression("!");
	public static CodeExpression Negate(CodeExpression expr) {
		var prim = expr as CodePrimitiveExpression;
		if(prim != null && prim.Value is bool)
			return new CodePrimitiveExpression(!(bool)prim.Value);
		var invoke = expr as CodeDelegateInvokeExpression;
		if(invoke != null && invoke.TargetObject == opNegation)
			return invoke.Parameters[0];
		return new CodeDelegateInvokeExpression(opNegation, expr); 
	}
	
	static Dictionary<System.Type, int> vectorTypeSizes = new Dictionary<System.Type, int>(){
		{typeof(Vector2), 2}, {typeof(Vector2Int), 2},
		{typeof(Vector3), 3}, {typeof(Vector3Int), 3},
		{typeof(Vector4), 4}, {typeof(Quaternion), 4},
		{typeof(Color32), 4}, {typeof(Color), 4}, 
	};
	
	public static CodeExpression Ref(object value) {
		var type = value?.GetType();
		if(value == null || type.IsPrimitive || value is string || value is decimal)
			return new CodePrimitiveExpression(value);
		if(type.IsValueType) {
			if(type.IsEnum) {
				var name = System.Enum.GetName(type, value);
				if(name == null)
					return new CodeCastExpression(type, new CodePrimitiveExpression((int)value));
				return new CodePropertyReferenceExpression(new CodeTypeReferenceExpression(type), name);
			}
			int size;
			if(vectorTypeSizes.TryGetValue(type, out size)) {
				var indexer = type.GetProperty("Item");
				var expr = new CodeObjectCreateExpression(type);
				for(int i=0; i<size; i++)
					expr.Parameters.Add(new CodePrimitiveExpression(indexer.GetValue(value, new object[]{i})));
				return expr;
			}
			// TODO: Bounds, Rect, RectInt, LayerMask
		} else {
			switch(value) {
			case System.Type t:
				return new CodeTypeOfExpression(t);
			case UdonGameObjectComponentHeapReference heapRef:
				switch(heapRef.type.Name) {
				case nameof(GameObject):
					return new CodePropertyReferenceExpression(new CodeThisReferenceExpression(), "gameObject");
				case nameof(Transform):
					return new CodePropertyReferenceExpression(new CodeThisReferenceExpression(), "transform");
				default: // UdonBehaviour, Object
					return new CodeThisReferenceExpression();
				}
			}
		}
		return null;
	}
	static bool IsArrayZero(System.Array array) {
		var elemType = array.GetType().GetElementType();
		var elemDef = elemType.IsValueType ? System.Activator.CreateInstance(elemType) : null;
		var n = array.GetLength(0);
		for(int i=0; i<n; i++)
			if(!object.Equals(array.GetValue(i), elemDef))
				return false;
		return true;
	}
	public static CodeExpression Value(object value) {
		var refValue = Ref(value);
		if(refValue != null)
			return refValue;
		var type = value.GetType();
		if(type.IsArray) {
			// var elemType = type.GetElementType();
			// var elemDef = elemType.IsValueType ? System.Activator.CreateInstance(elemType) : null;
			// var arr = (System.Array)value;
			// var n = arr.GetLength(0);
			// for(int i=0; i<n; i++)
			// 	if(!arr.GetValue(i).Equals(elemDef)) {
			// 		var exprs = new CodeExpression[n];
			// 		for(int j=0; j<n; j++)
			// 			exprs[j] = Value(arr.GetValue(j));
			// 		return new CodeArrayCreateExpression(type, exprs);
			// 	}
			// return new CodeArrayCreateExpression(type, n);

			var array = (System.Array)value;
			var expr = new CodeArrayCreateExpression(type, array.GetLength(0));
			if(!IsArrayZero(array))
				for(int i=0; i<expr.Size; i++)
					expr.Initializers.Add(Value(array.GetValue(i)));
			return expr;
		}
		switch(value) {
		case VRC.SDKBase.VRCUrl url:
			return new CodeObjectCreateExpression(type, new CodePrimitiveExpression(url.Get()));
		// TODO: AnimationCurve, Gradient
		}
		return new CodeSnippetExpression($"???{value}???");
	}
	public static bool SideEffect(CodeExpression expr) {
		switch(expr) {
		case CodeTypeReferenceExpression _:
		case CodeTypeOfExpression _:
		case CodeVariableReferenceExpression _:
		case CodePrimitiveExpression _:
			return false;

		case CodeBinaryOperatorExpression e:
			return SideEffect(e.Left) || SideEffect(e.Right);
		case CodeCastExpression e:
			return SideEffect(e.Expression);
		case CodePropertyReferenceExpression e:
			return SideEffect(e.TargetObject);

		case CodeArrayIndexerExpression e:
			foreach(CodeExpression x in e.Indices)
				if(SideEffect(x))
					return true;
			return SideEffect(e.TargetObject);
		case CodeIndexerExpression e:
			foreach(CodeExpression x in e.Indices)
				if(SideEffect(x))
					return true;
			return SideEffect(e.TargetObject);

		case CodeArrayCreateExpression e:
			foreach(CodeExpression x in e.Initializers)
				if(SideEffect(x))
					return true;
			return SideEffect(e.SizeExpression);
		case CodeObjectCreateExpression e:
			foreach(CodeExpression x in e.Parameters)
				if(SideEffect(x))
					return true;
			return false;
		case CodeMethodInvokeExpression e:
			foreach(CodeExpression x in e.Parameters)
				if(SideEffect(x))
					return true;
			return SideEffect(e.Method.TargetObject);
		
		default:
			return true;
		}
	}
	public static string GenerateCode(CodeObject obj) {
		using (var writer = new System.IO.StringWriter()) {
			using (var provider = CodeDomProvider.CreateProvider("CSharp")) {
				var options = new CodeGeneratorOptions();
				// options.BracingStyle = "Block";
				options.IndentString = "\t";
				options.BlankLinesBetweenMembers = false;
				if(obj is CodeExpression)
					provider.GenerateCodeFromExpression(obj as CodeExpression, writer, options);
				else if(obj is CodeStatement)
					provider.GenerateCodeFromStatement(obj as CodeStatement, writer, options);
				else if(obj is CodeTypeMember)
					provider.GenerateCodeFromMember(obj as CodeTypeMember, writer, options);
				return writer.ToString();
			}
		}
	}
}
}