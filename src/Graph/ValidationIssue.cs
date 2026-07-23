using KSP.Localization;

namespace KRAB.Graph
{
	public enum IssueSeverity
	{
		Info,
		Warning,
		Error
	}

	/// <summary>
	/// A single graph validation finding. <see cref="message"/> is plain English,
	/// used only for logging (KrabEvaluator's "compiled with error(s)" log line,
	/// KSP.log in general) — code/log text stays English by convention regardless
	/// of the player's language. <see cref="LocalizedText"/> is the in-UI
	/// equivalent, built from <see cref="code"/> plus <see cref="args"/> via
	/// #LOC_KRAB_issue_&lt;code&gt; (in-game report, 2026-07-23: the editor's
	/// validation strip was showing this raw English message directly).
	/// </summary>
	public class ValidationIssue
	{
		public readonly IssueSeverity severity;
		public readonly string code;
		public readonly string message;
		public readonly string[] args;

		/// <summary>Id of the node the issue is about, when node-specific (UI highlight).</summary>
		public readonly string nodeId;

		public ValidationIssue(IssueSeverity severity, string code, string message, string nodeId = null,
			params string[] args)
		{
			this.severity = severity;
			this.code = code;
			this.message = message;
			this.nodeId = nodeId;
			this.args = args ?? new string[0];
		}

		/// <summary>Localized text for the editor's validation strip. Falls back to the
		/// English log message if a code has no #LOC_KRAB_issue_ entry (defensive —
		/// every code defined in KrabGraph.Validate() has one; this only guards
		/// against the two drifting apart in the future).</summary>
		public string LocalizedText()
		{
			string localized = Localizer.Format("#LOC_KRAB_issue_" + code, args);
			return localized.StartsWith("#LOC_KRAB_") ? message : localized;
		}

		public override string ToString()
		{
			return "[" + severity + "] " + code + ": " + message;
		}
	}
}
