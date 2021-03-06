﻿// GraphView
// 
// Copyright (c) 2015 Microsoft Corporation
// 
// All rights reserved. 
// 
// MIT License
// 
// Permission is hereby granted, free of charge, to any person obtaining a copy of
// this software and associated documentation files (the "Software"), to deal in
// the Software without restriction, including without limitation the rights to
// use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of
// the Software, and to permit persons to whom the Software is furnished to do so,
// subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be included in all
// copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS
// FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR
// COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER
// IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN
// CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
// 
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using Microsoft.Win32.SafeHandles;

namespace GraphView
{
    public partial class GraphViewConnection : IDisposable
    {
        private string supperNode;

        private Dictionary<string, Int64> _dictionaryTableId; // <Table Name> => <Table Id>
        private Dictionary<string, int> _dictionaryTableOffsetId; // <Table Name> => <Table Offset Id> (counted from 0)
        private Dictionary<Tuple<string, string>, Int64> _dictionaryColumnId; // <Table Name, Column Name> => <Column Id>

        private static readonly List<string> ColumnList =
            new List<string>
            {
                "GlobalNodeId",
                "ReversedEdge",
                "ReversedEdgeDeleteCol"
            };

        private Dictionary<Tuple<string, string>, int> _dictionaryEdges; //<NodeTable, Edge> => ColumnId
        private Dictionary<string, List<long>> _dictionaryAttribute; //<EdgeViewAttributeName> => List<AttributeId>
        private Dictionary<string, string> _attributeType; //<EdgeViewAttribute> => <Type>

        /// <summary>
        /// Creates node view.
        /// This operation creates view on nodes, mapping native node table propeties into node view 
        /// and merging all the edge columns relevant to the nodes in the view.
        /// </summary>
        /// <param name="tableSchema"> The Schema name of node table. Default(null or "") by "dbo".</param>
        ///  <param name="nodeViewName"> The name of supper node. </param>
        /// <param name="nodes"> The list of the names of native nodes. </param>
        /// <param name="propertymapping"> 
        /// Type is List<Tuple<string, List<Tuple<string, string>>>>
        /// That is, List<Tuple<nodeViewpropety, List<Tuple<native node table name, native node table propety>>>> 
        /// </param>
        /// <param name="externalTransaction">An existing SqlTransaction instance under which the create node view will occur.</param>
        public void createNodeView(string tableSchema, string nodeViewName, List<string> nodes,
            List<Tuple<string, List<Tuple<string, string>>>>  propertymapping,
            SqlTransaction externalTransaction = null)
        {
            //Check validity of input
            if (string.IsNullOrEmpty(tableSchema))
            {
                tableSchema = "dbo";
            }
            if (string.IsNullOrEmpty(nodeViewName))
            {
                throw new EdgeViewException("The string of supper node name is null or empty.");
            }
            if (nodes == null || !nodes.Any())
            {
                throw new EdgeViewException("The list of nodes is null or empty.");
            }
            if (propertymapping == null || !propertymapping.Any())
            {
                throw new EdgeViewException("The list of property mapping is null or empty.");
            }


            _dictionaryTableId = new Dictionary<string, long>(); //Also for searching table record in meta table
            _dictionaryTableOffsetId = new Dictionary<string, int>(); //Also for searching user-given node table
            _dictionaryColumnId = new Dictionary<Tuple<string, string>, long>(); //Also for searching user-given node view's property

            SqlTransaction transaction;
            transaction = externalTransaction ?? Conn.BeginTransaction();
            var command = Conn.CreateCommand();
            command.Transaction = transaction;

            try
            {
                //Check validity of the list of node tables and get their table ID.
                const string getTableId = @"
            Select TableName, TableId
            From [dbo].{0}
            Where TableSchema = @schema";
                command.CommandText = String.Format(getTableId, MetadataTables[0]); //GraphTable
                command.Parameters.Add("schema", SqlDbType.NVarChar, 128);
                command.Parameters["schema"].Value = tableSchema;

                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var tableName = reader["TableName"].ToString().ToLower();
                        var tableId = Convert.ToInt64(reader["TableId"].ToString());
                        if (nodes.Exists(x => (x.ToLower() == tableName)))
                        {
                            _dictionaryTableId[tableName] = tableId;
                        }
                    }
                }
                if (_dictionaryTableId.Count() != nodes.Count())
                {
                    foreach (var it in nodes)
                    {
                        if (!_dictionaryTableId.ContainsKey(it))
                        {
                            throw new NodeViewException(string.Format("The graph table [{0}].[{1}] is not found",
                                tableSchema, it));
                        }
                    }
                }

                var count = 0;
                foreach (var it in nodes)
                {
                    if (_dictionaryTableOffsetId.ContainsKey(it.ToLower()))
                    {
                        throw new NodeViewException(string.Format("Node table {0} is given twice", it));
                    }
                    _dictionaryTableOffsetId[it.ToLower()] = count;
                    count++;
                }

                //Check validity of the proper mapping and get their column ID.
                foreach (var it in propertymapping)
                {
                    foreach (var VARIABLE in it.Item2)
                    {
                        _dictionaryColumnId[Tuple.Create(VARIABLE.Item1.ToLower(), VARIABLE.Item2.ToLower())] = -1;
                    }
                }

                const string getColumnId = @"
                Select ColumnId, TableName, ColumnName, ColumnRole
                From {0}
                Where TableSchema = @schema";
                command.CommandText = string.Format(getColumnId, MetadataTables[1]); //_NodeTableColumnCollection

                var edgeCount = 0;
                var edgeList = new List<Tuple<string, string>>[nodes.Count];
                for (int i = 0; i < nodes.Count; i++)
                {
                    edgeList[i] = new List<Tuple<string, string>>();
                }

                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var tableName = reader["TableName"].ToString().ToLower();
                        var columnId = Convert.ToInt64(reader["ColumnId"].ToString());
                        var columnName = reader["ColumnName"].ToString().ToLower();
                        var columnRole = Convert.ToInt32(reader["ColumnRole"].ToString());

                        if (columnRole == 2 || columnRole == 0)
                        {
                            var columnTuple = Tuple.Create(tableName, columnName);
                            if (_dictionaryColumnId.ContainsKey(columnTuple))
                            {
                                _dictionaryColumnId[columnTuple] = columnId;
                            }
                        }
                        else
                        {
                            if (columnRole == 1 && _dictionaryTableOffsetId.ContainsKey(tableName))
                            {
                                edgeList[_dictionaryTableOffsetId[tableName]].Add(Tuple.Create(tableName, columnName));
                                edgeCount++;
                            }
                        }
                    }
                }
                foreach (var it in _dictionaryColumnId)
                {
                    if (it.Value == -1)
                    {
                        throw new EdgeViewException(string.Format("The column [{0}].[{1}].[{2}] is not found.",
                            tableSchema, it.Key.Item1, it.Key.Item2));
                    }
                }

                //Product create view string.
                count = 0;
                var mapping2DArrayTuples = new Tuple<string, string>[nodes.Count()][];
                for (int i = 0; i < nodes.Count; i++)
                {
                    mapping2DArrayTuples[i] = new Tuple<string, string>[propertymapping.Count];
                }

                foreach (var it in propertymapping)
                {
                    foreach (var variable in it.Item2)
                    {
                        if (!_dictionaryTableOffsetId.ContainsKey(variable.Item1.ToLower()))
                        {
                            throw new Exception(
                                string.Format("The table column [{0}].[{1}] in propety mapping is not found.",
                                    tableSchema, variable.Item1));
                        }
                        mapping2DArrayTuples[_dictionaryTableOffsetId[variable.Item1.ToLower()]][count] =
                            Tuple.Create(variable.Item2, it.Item1);
                    }
                    count++;
                }
                var selectStringList = new string[nodes.Count];
                const string selectTemplate = "Select GlobalNodeId, InDegree, LocalNodeId{0}\n" +
                                              "From {1}\n";
                int edgeColumnOffset = 0;
                var edgeNameList = new string[edgeCount];
                edgeCount = 0;
                for (int i = 0; i < nodes.Count; i++)
                {
                    foreach (var it in edgeList[i])
                    {
                        edgeNameList[edgeCount] = it.Item1 + "_" + it.Item2;
                        edgeCount++;
                    }
                }

                int row = 0;
                foreach (var it in nodes)
                {
                    string selectElement;
                    var elementList =
                        mapping2DArrayTuples[row].Select(
                            item => (item != null ? item.Item1.ToString() : "null") + " as " + item.Item2).ToList();

                    for (int i = 0; i < edgeColumnOffset; i++)
                    {
                        elementList.Add("null as " + edgeNameList[i]);
                    }

                    foreach (var variable in edgeList[row])
                    {
                        elementList.Add(variable.Item2 + " as " + variable.Item1 + '_' + variable.Item2);
                    }

                    edgeColumnOffset += edgeList[row].Count;

                    for (int i = edgeColumnOffset; i < edgeCount; i++)
                    {
                        elementList.Add("null as " + edgeNameList[i]);
                    }

                    selectElement = string.Join(",", elementList);
                    if (!string.IsNullOrEmpty(selectElement))
                    {
                        selectElement = ", " + selectElement;
                    }
                    selectStringList[row] = string.Format(selectTemplate, selectElement, it);
                    row++;
                }


                const string createView = "Create View {0} as(\n" +
                                          "{1}" +
                                          ")\n";
                command.Parameters.Clear();
                command.CommandText = string.Format(createView, nodeViewName,
                    string.Join("Union all\n", selectStringList));
                command.ExecuteNonQuery();

                //Update metaTable
                Int64 nodeviewTableId = 0;
                string updateGraphTable =
                    string.Format(@"INSERT INTO {0} OUTPUT [Inserted].[TableId] VALUES (@schema, @nodeviewname, 1)",
                        MetadataTables[0]);
                command.Parameters.AddWithValue("schema", tableSchema);
                command.Parameters.AddWithValue("nodeviewname", nodeViewName);
                command.CommandText = updateGraphTable;
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        nodeviewTableId = Convert.ToInt64(reader["tableId"].ToString());
                    }
                }

                var nodeViewPropertycolumnId = new List<Int64>();
                string updateTableColumn =
                    string.Format(@"
                    INSERT INTO {0} OUTPUT [Inserted].[ColumnId]
                    VALUES (@schema, @nodeviewname, @columnname, @columnrole, null)",
                        MetadataTables[1]);
                command.Parameters.AddWithValue("columnrole", 4);
                command.Parameters.Add("columnname", SqlDbType.NVarChar);
                command.CommandText = updateTableColumn;
                foreach (var it in propertymapping)
                {
                    command.Parameters["columnname"].Value = it.Item1;
                    using (var reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            nodeViewPropertycolumnId.Add(Convert.ToInt64(reader["ColumnId"].ToString()));
                        }
                    }
                }


                string updateNodeView = string.Format(@"INSERT INTO {0} VALUES (@nodeviewtableid, @tableid)",
                    MetadataTables[7]);
                command.Parameters.Clear();
                command.Parameters.Add("nodeviewtableid", nodeviewTableId);
                command.Parameters.Add("tableid", SqlDbType.NVarChar);
                command.CommandText = updateNodeView;
                foreach (var it in nodes)
                {
                    command.Parameters["tableid"].Value = _dictionaryTableId[it.ToLower()];
                    command.ExecuteNonQuery();
                }


                string updateNodeViewcolumn = string.Format(@"INSERT INTO {0} VALUES (@nodeviewcolumnid, @columnid)",
                    MetadataTables[5]);
                command.Parameters.Clear();
                command.Parameters.Add("nodeviewcolumnid", SqlDbType.BigInt);
                command.Parameters.Add("columnid", SqlDbType.BigInt);
                command.CommandText = updateNodeViewcolumn;
                int posi = 0;
                foreach (var it in propertymapping)
                {
                    command.Parameters["nodeviewcolumnid"].Value = nodeViewPropertycolumnId[posi];
                    foreach (var variable in it.Item2)
                    {
                        var columnTuple = Tuple.Create(variable.Item1.ToLower(), variable.Item2.ToLower());
                        command.Parameters["columnid"].Value = _dictionaryColumnId[columnTuple];
                        command.ExecuteNonQuery();
                    }
                    posi++;
                }
                if (externalTransaction == null)
                {
                    transaction.Commit();
                }
            }
            catch (Exception error)
            {
                if (externalTransaction == null)
                {
                    transaction.Rollback();
                }
                throw new NodeViewException("Create node view:" + error.Message);
            }
        }


        /// <summary>
        /// Drop Edge View
        /// </summary>
        /// <param name="nodeViewSchema">The name of node view. Default(null or "") by "dbo".</param>
        /// <param name="nodeViewName">The name of node view.</param>
        /// <param name="externalTransaction">An existing SqlTransaction instance under which the drop edge view will occur.</param>
        public void DropNodeView(string nodeViewSchema, string nodeViewName, SqlTransaction externalTransaction = null)
        {
            SqlTransaction transaction = externalTransaction ?? Conn.BeginTransaction();
            var command = Conn.CreateCommand();
            command.Transaction = transaction;
            try
            {
                //drop view
                const string dropView = @"drop view [{0}]";
                command.CommandText = string.Format(dropView, nodeViewName);
                command.ExecuteNonQuery();

                //update metatable
                const string deleteNodeViewColumn = @"
                Delete from [{0}]  
                where [NodeViewColumnId] in
                (
                    Select columnId
                    From [{1}] NodeTableColumn
                    Where TableSchema = @schema and  TableName = @tablename and ColumnRole = @role
                )
                Delete from [{1}]
                Where TableSchema = @schema and  TableName = @tablename and ColumnRole = @role";
                command.Parameters.AddWithValue("schema", nodeViewSchema);
                command.Parameters.AddWithValue("tablename", nodeViewName);
                command.Parameters.AddWithValue("role", 4);
                command.CommandText = string.Format(deleteNodeViewColumn, MetadataTables[5], MetadataTables[1]);
                command.ExecuteNonQuery();

                const string deleteNodeView = @"
                Delete from [{0}]
                where NodeViewTableId in
                (
                    select tableid 
                    from [{1}]
                    where TableRole = @role and TableSchema = @schema and TableName = @tablename
                )
                delete from [{1}]
                where TableRole = @role and TableSchema = @schema and TableName = @tablename";
                command.Parameters["role"].Value = 1;
                command.CommandText = string.Format(deleteNodeView, MetadataTables[7], MetadataTables[0]);
                command.ExecuteNonQuery();
                if (externalTransaction == null)
                {
                    transaction.Commit();
                }
            }
            catch (Exception error)
            {
                if (externalTransaction == null)
                {
                    transaction.Rollback();
                }
                throw new NodeViewException("Drop node view:" + error.Message);
            }
        }

        /// <summary>
        /// Create view on edges
        /// </summary>
        /// <param name="tableSchema"> The Schema name of node table. Default(null or "") by "dbo".</param>
        ///  <param name="supperNodeName"> The name of supper node. </param>
        /// <param name="edgeViewName"> The name of supper edge. </param>
        /// <param name="edges"> The list of message of edges for merging.
        /// The message is stored in tuple, containing (node table name, edge column name).</param>
        /// <param name="edgeAttribute"> The attributes' names in the supper edge.</param>
        /// <param name="attributeMapping"> User-supplied attribute-mapping.
        ///  Type is List<Tuple<string, List<Tuple<string, string, string>>>>.
        ///  That is, every attribute in supper edge is mapped into a list of attributes,
        ///  with the message of <node table name, edge column name, attribute name>
        ///  If one attribute in supper edge need to be mapped into all the user-supplied edges's same-name attributes,
        ///  user can pass a null or empty parameter of  List<Tuple<string, string, string>>.
        ///  When "attributeMapping" is empty or null, the program will map the atrributes of supper edge
        ///  into all the same-name attributes of all the user-supplied edges.</param>
        public void CreateEdgeView(string tableSchema, string supperNodeName, string edgeViewName, 
            List<Tuple<string, string>> edges, List<string> edgeAttribute,
            List<Tuple<string, List<Tuple<string, string, string>>>> attributeMapping = null)
        {
            supperNode = supperNodeName;
            var transaction = Conn.BeginTransaction();
            var command = Conn.CreateCommand();
            command.CommandTimeout = 0;
            command.Transaction = transaction;
#if DEBUG
            transaction.Commit();
#endif
            try
            {
                CreateEdgeViewDecoder(tableSchema, edgeViewName, edges, edgeAttribute, command, attributeMapping);
                updateEdgeViewMetaData(tableSchema, edgeViewName, command);
#if !DEBUG
            transaction.Commit();
#endif
            }
            catch (Exception error)
            {
                throw new EdgeViewException(error.Message);
            }
        }

        ///  <summary>
        ///  Edge View: merge servel edges into supper edge
        ///  </summary>
        ///  <param name="tableSchema"> The Schema name of node table. Default(null or "") by "dbo".</param>
        ///  <param name="edgeViewName"> The name of supper edge. </param>
        ///  <param name="edges"> The list of message of edges for merging.
        ///  The message is stored in tuple, containing (node table name, edge column name).</param>
        ///  <param name="edgeAttribute"> The attributes' names in the supper edge.</param>
        /// <param name="command"> SqlCommand </param>
        /// <param name="attributeMapping"> User-supplied attribute-mapping.
        ///  Type is List<Tuple<string, List<Tuple<string, string, string>>>>.
        ///  That is, every attribute in supper edge is mapped into a list of attributes,
        ///  with the message of <node table name, edge column name, attribute name>
        ///  If one attribute in supper edge need to be mapped into all the user-supplied edges's same-name attributes,
        ///  user can pass a null or empty parameter of  List<Tuple<string, string, string>>.
        ///  When "attributeMapping" is empty or null, the program will map the atrributes of supper edge
        ///  into all the same-name attributes of all the user-supplied edges.</param>
        private void CreateEdgeViewDecoder(string tableSchema, string edgeViewName, List<Tuple<string, string>> edges,
            List<string> edgeAttribute, SqlCommand command,
            List<Tuple<string, List<Tuple<string, string, string>>>> attributeMapping = null)
        {
            //Check validity of input
            if (string.IsNullOrEmpty(tableSchema))
            {
                tableSchema = "dbo";
            }
            if (string.IsNullOrEmpty(edgeViewName))
            {
                throw new EdgeViewException("The string of edge view name is null or empty.");
            }
            if (edges == null || !edges.Any())
            {
                throw new EdgeViewException("The list of edges is null or empty.");
            }
            if (edgeAttribute == null)
            {
                edgeAttribute = new List<string>();
            }

            _dictionaryEdges = edges.ToDictionary(x => Tuple.Create(x.Item1.ToLower(), x.Item2.ToLower()), x => -1);
            //<NodeTable, Edge> => ColumnId

            //Check validity of table name in metaDataTable and get table's column id
            command.Parameters.Clear();
            const string checkEdgeColumn = @"
                select *
                from {0}
                where TableSchema = @tableschema and ColumnRole = @role";
            command.CommandText = string.Format(checkEdgeColumn, MetadataTables[1]); //_NodeTableColumnCollection
            command.Parameters.Add("tableschema", SqlDbType.NVarChar, 128);
            command.Parameters["tableschema"].Value = tableSchema;
            command.Parameters.Add("role", SqlDbType.Int);
            command.Parameters["role"].Value = 1;
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    var tableName = reader["TableName"].ToString().ToLower();
                    var edgeColumnName = reader["ColumnName"].ToString().ToLower();
                    int columnId = Convert.ToInt32(reader["ColumnId"].ToString());
                    var edgeTuple = Tuple.Create(tableName, edgeColumnName);
                    if (_dictionaryEdges.ContainsKey(edgeTuple))
                    {
                        _dictionaryEdges[edgeTuple] = columnId;
                    }
                }
            }
            var edgeNotInMetaTable = _dictionaryEdges.Where(x => x.Value == -1).Select(x => x.Key).ToArray();
            if (edgeNotInMetaTable.Any())
            {
                throw new EdgeViewException(string.Format("There doesn't exist edge column \"{0}.{1}\"",
                    edgeNotInMetaTable[0].Item1, edgeNotInMetaTable[0].Item2));
            }

            //Check validity of edge view name
            const string checkEdgeViewName = @"
                select *
                from {0}
                where TAbleSchema = @schema and TableName = @tablename and ColumnName = @columnname and ColumnRole = @role and Reference = @ref";
            command.Parameters.Clear();
            command.Parameters.AddWithValue("schema", tableSchema);
            command.Parameters.AddWithValue("tablename", supperNode);
            command.Parameters.AddWithValue("columnname", edgeViewName);
            command.Parameters.AddWithValue("role", 3);
            command.Parameters.AddWithValue("ref", supperNode);
            command.CommandText = String.Format(checkEdgeViewName, MetadataTables[1]); //_NodeTableColumnCollection
            using (var reader = command.ExecuteReader())
            {
                if (reader.Read())
                {
                    throw new EdgeViewException(string.Format("Edge view name \"{0}\" alreadly existed.", edgeViewName));
                }
            }

            //Get the attribute's ids which refer to user-supplied attribute in meta data table
            _dictionaryAttribute = edgeAttribute.ToDictionary(x => x.ToLower(), x => new List<Int64>());
            //<EdgeViewAttributeName> => List<AttributeId>

            _attributeType = edgeAttribute.ToDictionary(x => x.ToLower(), x => "");
            //<EdgeViewAttribute> => <Type>

            var edgesAttributeMappingDictionary =
                edges.ToDictionary(x => Tuple.Create(x.Item1.ToLower(), x.Item2.ToLower()),
                    x => new List<Tuple<string, string>>());
            //<nodeTable, edgeName> => list<Tuple<Type, EdgeViewAttributeName>>

            const string getAttributeId = @"
                Select *
                From {0}
                Where TableSchema = @schema
                Order by AttributeEdgeId";
            command.Parameters.Clear();
            command.Parameters.Add("schema", SqlDbType.NVarChar, 128);
            command.Parameters["schema"].Value = tableSchema;
            command.CommandText = String.Format(getAttributeId, MetadataTables[2]); //_EdgeAttributeCollection

            //User supplies attribute mapping or not.
            if (attributeMapping == null || !attributeMapping.Any())
            {
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var tableName = reader["TableName"].ToString().ToLower();
                        var edgeColumnName = reader["ColumnName"].ToString().ToLower();
                        var edgeTuple = Tuple.Create(tableName, edgeColumnName);
                        if (_dictionaryEdges.ContainsKey(edgeTuple))
                        {
                            var type = reader["AttributeType"].ToString().ToLower();
                            var attributeMappingTo = "";

                            var attributeName = reader["AttributeName"].ToString().ToLower();
                            if (_dictionaryAttribute.ContainsKey(attributeName))
                            {
                                var attributeId = Convert.ToInt64(reader["AttributeId"].ToString());
                                _dictionaryAttribute[attributeName].Add(attributeId);
                                if (_attributeType[attributeName] == "")
                                {
                                    _attributeType[attributeName] = type;
                                }
                                else if (_attributeType[attributeName] != type)
                                {
                                    throw new EdgeViewException(
                                        string.Format(
                                            "There exist two edge attributes \"{0}\" with different type in different edges.",
                                            attributeName));
                                }
                                attributeMappingTo = attributeName;
                            }
                            edgesAttributeMappingDictionary[edgeTuple].Add(Tuple.Create(type, attributeMappingTo));
                        }
                    }
                }
            }
            else
            {
                var attributeMappingIntoDictionary = new Dictionary<Tuple<string, string, string>, string>();
                // <TableName, EdgeName, Attribute> => <EdgeViewAttribute>

                foreach (var it  in attributeMapping)
                {
                    if (it.Item2 != null && it.Item2.Any())
                    {
                        foreach (var itAttributeRef in it.Item2)
                        {
                            var tempAttributeRef = Tuple.Create(itAttributeRef.Item1.ToLower(),
                                itAttributeRef.Item2.ToLower(),
                                itAttributeRef.Item3.ToLower());
                            if (attributeMappingIntoDictionary.ContainsKey(tempAttributeRef))
                            {
                                throw new EdgeViewException(
                                    string.Format(
                                        "The attribute \"{0}.{1}.{2}\" maps into edge view's attribute twice.",
                                        itAttributeRef.Item1, itAttributeRef.Item2, itAttributeRef.Item3));
                            }
                            attributeMappingIntoDictionary[tempAttributeRef] = it.Item1;
                        }
                    }
                    else
                    {
                        foreach (var itAttributeRef in edges)
                        {
                            var tempAttributeRef = Tuple.Create(itAttributeRef.Item1.ToLower(),
                                itAttributeRef.Item2.ToLower(),
                                it.Item1.ToLower());
                            if (attributeMappingIntoDictionary.ContainsKey(tempAttributeRef))
                            {
                                throw new EdgeViewException(
                                    string.Format(
                                        "The attribute \"{0}.{1}.{2}\" maps into edge view's attribute twice.",
                                        itAttributeRef.Item1, itAttributeRef.Item2, it.Item1));
                            }
                            attributeMappingIntoDictionary[tempAttributeRef] = it.Item1;
                        }
                    }
                }
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var tableName = reader["TableName"].ToString().ToLower();
                        var edgeColumnName = reader["ColumnName"].ToString().ToLower();
                        var edgeTuple = Tuple.Create(tableName, edgeColumnName);
                        
                        if (_dictionaryEdges.ContainsKey(edgeTuple))
                        {
                            var type = reader["AttributeType"].ToString().ToLower();
                            var attributeMappingTo = "";
                            var attributeName = reader["AttributeName"].ToString().ToLower();

                            var attributeTuple = Tuple.Create(tableName, edgeColumnName, attributeName);
                            
                            if (attributeMappingIntoDictionary.ContainsKey(attributeTuple))
                            {
                                var edgeViewAttributeName = attributeMappingIntoDictionary[attributeTuple];
                                var attributeId = Convert.ToInt64(reader["AttributeId"].ToString());
                                _dictionaryAttribute[edgeViewAttributeName].Add(attributeId);
                                if (_attributeType[edgeViewAttributeName] == "")
                                {
                                    _attributeType[edgeViewAttributeName] = type;
                                }
                                else if (_attributeType[edgeViewAttributeName] != type)
                                {
                                    throw new EdgeViewException(
                                        string.Format(
                                            "There exist two edge attributes \"{0}\" with different type in different edges.",
                                            edgeViewAttributeName));
                                }
                                attributeMappingTo = attributeMappingIntoDictionary[attributeTuple];
                            }
                            edgesAttributeMappingDictionary[edgeTuple].Add(Tuple.Create(type, attributeMappingTo));
                        }
                    }
                }
            }
            var emptyAttribute = _attributeType.Select(x => x).Where(x => (x.Value == "")).ToArray();
            if (emptyAttribute.Any())
            {
                throw new EdgeViewException(string.Format("There is not attribute for \"{0}\" to map into",
                    emptyAttribute[0].Key));
            }

            GraphViewDefinedFunctionGenerator.RegisterEdgeView(supperNode, tableSchema, edgeViewName, _attributeType,
                edgesAttributeMappingDictionary, Conn, command.Transaction);
        }

        /// <param name="tableSchema"> The Schema name of node table. Default(null or "") by "dbo".</param>
        /// <param name="edgeViewName"> The name of supper edge. </param>
        /// <param name="command"> Sql Command </param>
        private void updateEdgeViewMetaData(string tableSchema, string edgeViewName, SqlCommand command)
        {

                //Insert edge view message into "_NodeTableColumnCollection" MetaDataTable
                const string insertGraphEdgeView = @"
                INSERT INTO [{0}] ([TableSchema], [TableName], [ColumnName], [ColumnRole], [Reference])
                OUTPUT [Inserted].[ColumnId]
                VALUES (@tableshema, @TableName, @columnname, @columnrole, @reference)";
                command.Parameters.Clear();
                command.Parameters.AddWithValue("tableshema", tableSchema);
                command.Parameters.AddWithValue("tablename", supperNode);
                command.Parameters.AddWithValue("columnname", edgeViewName);
                command.Parameters.AddWithValue("columnrole", 3);
                command.Parameters.AddWithValue("reference", supperNode);

                command.CommandText = string.Format(insertGraphEdgeView, MetadataTables[1]); //_NodeTableColumnCollection
                Int64 edgeViewId;
                using (var reader = command.ExecuteReader())
                {
                    if (!reader.Read())
                    {
                        throw new EdgeViewException(string.Format("MetaDataTable \"{0}\" can not be inserted", MetadataTables[0]));
                    }
                    edgeViewId = Convert.ToInt64(reader["ColumnId"], CultureInfo.CurrentCulture);
                }

                //Insert the edges's message into "_EdgeViewCollection" MetaDataTable
                const string insertEdgeViewCollection = @"
                INSERT INTO [{0}]
                VALUES (@NodeViewColumnId, @ColumnId)";
                command.Parameters.Clear();
                command.Parameters.Add("NodeViewColumnId", SqlDbType.BigInt);
                command.Parameters["NodeViewColumnId"].Value = edgeViewId;
                command.Parameters.Add("ColumnId", SqlDbType.BigInt);
                command.CommandText = string.Format(insertEdgeViewCollection, MetadataTables[5]); //_EdgeViewCollection
                foreach (var it in _dictionaryEdges)
                {
                    command.Parameters["ColumnId"].Value = it.Value;
                    command.ExecuteNonQuery();
                }

                //Insert the edge view's attribute's message into "_EdgeAttributeCollection" MetaDataTable
                //Insert the message of edges which refer to user-supplied attribute into "_EdgeViewAttributeCollection" MetaDataTable
                const string insertEdgeViewAttribute = @"
                INSERT INTO [{0}] ([TableSchema], [TableName], [ColumnName], [AttributeName], [AttributeType], [AttributeEdgeId])
                OUTPUT [Inserted].[AttributeId]
                VALUES (@schema, @tablename, @columnname, @attributename, @type, @edgeid)";
                const string insertEdgeViewAttributeReference = @"
                INSERT INTO [{0}] ([EdgeViewAttributeId], [EdgeAttributeId])
                VALUES (@EdgeViewAttributeId, @EdgeAttributeId)";
                int count = 0;
                foreach (var it  in _dictionaryAttribute)
                {
                    command.Parameters.Clear();
                    command.Parameters.AddWithValue("schema", tableSchema);
                    command.Parameters.AddWithValue("tablename", supperNode);
                    command.Parameters.AddWithValue("columnname", edgeViewName);
                    command.Parameters.AddWithValue("attributename", it.Key);
                    command.CommandText = string.Format(insertEdgeViewAttribute, MetadataTables[2]);

                    //_EdgeAttributeCollection
                    command.Parameters.AddWithValue("type", _attributeType[it.Key].ToLower());
                    command.Parameters.AddWithValue("edgeid", count++);
                    Int64 edgeViewAttributeId ;
                    using (var reader = command.ExecuteReader())
                    {
                        if (!reader.Read())
                        {
                            throw new EdgeViewException("MetaDataTable \"Edge View AttributeId \" can not be inserted");
                        }
                        edgeViewAttributeId = Convert.ToInt64(reader["AttributeId"], CultureInfo.CurrentCulture);
                    }
                    command.Parameters.Clear();
                    command.Parameters.AddWithValue("EdgeViewAttributeId", edgeViewAttributeId);
                    command.Parameters.Add("EdgeAttributeId", SqlDbType.BigInt);
                    command.CommandText = string.Format(insertEdgeViewAttributeReference, MetadataTables[6]);

                    //_EdgeViewAttributeCollection
                    foreach (var variable in it.Value)
                    {
                        command.Parameters["EdgeAttributeId"].Value = variable;
                        command.ExecuteNonQuery();
                    }
                }
        }

        /// <summary>
        /// Drop Edge View
        /// </summary>
        /// <param name="tableSchema">The name of schema. Default(null or "") by "dbo".</param>
        /// <param name="tableName">The name of node table.</param>
        /// <param name="edgeView">The name of Edge View</param>
        /// <param name="externalTransaction">An existing SqlTransaction instance under which the drop edge view will occur.</param>
        public void DropEdgeView(string tableSchema, string tableName, string edgeView, SqlTransaction externalTransaction = null)
        {
            SqlTransaction transaction;
            if (externalTransaction == null)
            {
                transaction = Conn.BeginTransaction();
            }
            else
            {
                transaction = externalTransaction;
            }
            var command = Conn.CreateCommand();
            command.Transaction = transaction;
            command.CommandTimeout = 0;
#if DEBUG
            if (externalTransaction == null)
            {
                transaction.Commit();
            }
#endif

            if (string.IsNullOrEmpty(tableSchema))
            {
                tableSchema = "dbo";
            }
            
            if (string.IsNullOrEmpty(tableName))
            {
                throw new EdgeViewException("The string of table name is null or empty.");
            }

            if (string.IsNullOrEmpty(edgeView))
            {
                throw new EdgeViewException("The string of edge view name is null or empty.");
            }
            try
            {
                supperNode = tableName;
                //Check validity of edge view name
                const string checkViewName = @"
                select *
                from {0}
                where TAbleSchema = @schema and TableName = @tablename and ColumnName = @columnname and ColumnRole = @role and Reference = @ref";
                command.Parameters.Clear();
                command.Parameters.AddWithValue("schema", tableSchema);
                command.Parameters.AddWithValue("tablename", supperNode);
                command.Parameters.AddWithValue("columnname", edgeView);
                command.Parameters.AddWithValue("role", 3);
                command.Parameters.AddWithValue("ref", supperNode);
                command.CommandText = String.Format(checkViewName, MetadataTables[1]); //_NodeTableColumnCollection
                using (var reader = command.ExecuteReader())
                {
                    if (!reader.Read())
                    {
                        throw new EdgeViewException(string.Format("Edge view name \"{0}.{1}.{2}\" is not existed.", tableSchema, supperNode,edgeView));
                    }
                }
                const string dropAttribtueRef = @"
                Delete from [{1}]
                Where [EdgeViewAttributeId] in
                (select GEA.[AttributeId]
                From [{0}] GEA
                Where [TableSchema] = @schema and [TableName] = @table and [ColumnName] = @column);";
                command.Parameters.Clear();
                command.Parameters.Add("schema", tableSchema);
                command.Parameters.Add("table", supperNode);
                command.Parameters.Add("column", edgeView);
                command.CommandText = string.Format(dropAttribtueRef, MetadataTables[2], MetadataTables[6]);
                command.ExecuteNonQuery();

                const string dropAttribtue = @"
                Delete From [{0}]
                Where [TableSchema] = @schema and [TableName] = @table and [ColumnName] = @column;";
                command.CommandText = string.Format(dropAttribtue, MetadataTables[2]);
                command.ExecuteNonQuery();

                const string dropEdgeViewCollection = @"
                Delete From [{1}]
                Where NodeViewColumnId in
                (Select [ColumnId]
                From [{0}]
                Where [TableSchema] = @schema and [TableName] = @table and [ColumnName] = @column);";
                command.CommandText = string.Format(dropEdgeViewCollection, MetadataTables[1], MetadataTables[5]);
                command.ExecuteNonQuery();

                const string dropNodeTableColumnCollection = @"
                Delete From [{0}]
                Where [TableSchema] = @schema and [TableName] = @table and [ColumnName] = @column;";
                command.CommandText = string.Format(dropNodeTableColumnCollection, MetadataTables[1]);
                command.ExecuteNonQuery();

                const string dropFunction = @"
                Drop function [{0}]";
                command.Parameters.Clear();
                command.CommandText = string.Format(dropFunction, tableSchema + '_' + supperNode + '_' + edgeView + '_' + "Decoder" );
                command.ExecuteNonQuery();
                const string dropAssembly = @"
                Drop Assembly [{0}_Assembly]";
                command.CommandText = string.Format(dropAssembly, tableSchema + '_' + supperNode + '_' + edgeView);
                command.ExecuteNonQuery();
#if !DEBUG
                if (externalTransaction == null)
                    transaction.Commit();
#endif
            }
            catch (Exception error)
            {
                if (externalTransaction == null)
                {
                    transaction.Rollback();
                }
                throw new EdgeViewException("Drop edge view:" + error.Message);
            }
        }
    }
}
