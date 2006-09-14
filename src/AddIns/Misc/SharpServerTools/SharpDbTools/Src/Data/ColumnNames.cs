// <file>
//     <copyright see="prj:///doc/copyright.txt"/>
//     <license see="prj:///doc/license.txt"/>
//     <owner name="Dickon Field" email=""/>
//     <version>$Revision: 1784 $</version>
// </file>

/*
 * User: dickon
 * Date: 30/07/2006
 * Time: 23:37
 * 
 */

using System;

namespace SharpDbTools.Data
{
	/// <summary>
	/// Description of Columns.
	/// </summary>
	public sealed class ColumnNames
	{
		public const string InvariantName = "invariantName";
		public const string Name = "name";
		public const string ConnectionString = "connectionString";
		public const string TableName = "TABLE_NAME";
		public static string[] TableTableFieldsToDisplay = 
			new string [] {"COLUMN_NAME", "DATATYPE", 
			"LENGTH", "PRECISION", "SCALE", "NULLABLE"};
		public static string[] TableTableFieldsColumnHeaders =
			new string[] { "Column", "Type", "Length", "Precision", "Scale", "Nullable" };
	}
}
