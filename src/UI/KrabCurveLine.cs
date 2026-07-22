using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace KRAB.UI
{
	/// <summary>
	/// Draws a polyline through a set of local-space points, built as a triangle
	/// strip in OnPopulateMesh — the standard way to render a line in pure UGUI
	/// with no external package (no LineRenderer/asset bundle: decision already
	/// made for the whole editor). Used for the curve preview in KrabCurveWindow.
	/// </summary>
	public class KrabCurveLine : MaskableGraphic
	{
		private readonly List<Vector2> points = new List<Vector2>();
		public float thickness = 2.2f;

		public void SetPoints(List<Vector2> newPoints)
		{
			points.Clear();
			points.AddRange(newPoints);
			SetVerticesDirty();
		}

		protected override void OnPopulateMesh(VertexHelper vh)
		{
			vh.Clear();
			if (points.Count < 2)
			{
				return;
			}
			for (int i = 0; i < points.Count - 1; i++)
			{
				Vector2 a = points[i];
				Vector2 b = points[i + 1];
				Vector2 dir = b - a;
				if (dir.sqrMagnitude < 1e-6f)
				{
					continue;
				}
				dir.Normalize();
				Vector2 normal = new Vector2(-dir.y, dir.x) * (thickness * 0.5f);

				UIVertex v0 = UIVertex.simpleVert;
				v0.color = color;
				v0.position = a - normal;
				UIVertex v1 = UIVertex.simpleVert;
				v1.color = color;
				v1.position = a + normal;
				UIVertex v2 = UIVertex.simpleVert;
				v2.color = color;
				v2.position = b + normal;
				UIVertex v3 = UIVertex.simpleVert;
				v3.color = color;
				v3.position = b - normal;

				int idx = vh.currentVertCount;
				vh.AddVert(v0);
				vh.AddVert(v1);
				vh.AddVert(v2);
				vh.AddVert(v3);
				vh.AddTriangle(idx, idx + 1, idx + 2);
				vh.AddTriangle(idx, idx + 2, idx + 3);
			}
		}
	}
}
