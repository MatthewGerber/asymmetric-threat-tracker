﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO;
using System.Diagnostics;
using LAIR.ResourceAPIs.PostgreSQL;

using Npgsql;
using System.Reflection;

namespace PTL.ATT.ShapeFiles
{
    public abstract class ShapeFile
    {
        public const string Table = "shape_file";

        public class Columns
        {
            [Reflector.Select(true)]
            public const string Id = "id";
            [Reflector.Insert, Reflector.Select(true)]
            public const string Name = "name";
            [Reflector.Insert, Reflector.Select(true)]
            public const string Type = "type";

            public static string Insert { get { return Reflector.GetInsertColumns(typeof(Columns)); } }
            public static string Select { get { return Reflector.GetSelectColumns(Table, typeof(Columns)); } }
        }

        [ConnectionPool.CreateTable]
        private static string CreateTable(ConnectionPool connection)
        {
            return "CREATE TABLE IF NOT EXISTS " + Table + " (" +
                   Columns.Id + " SERIAL PRIMARY KEY," +
                   Columns.Name + " VARCHAR," +
                   Columns.Type + " VARCHAR);";
        }

        protected static int Create(NpgsqlConnection connection, string name, Type type)
        {
            return Convert.ToInt32(new NpgsqlCommand("INSERT INTO " + Table + " (" + Columns.Insert + ") VALUES ('" + name + "','" + type + "') RETURNING " + Columns.Id, connection).ExecuteScalar());
        }

        public static void ImportShapeFiles(string[] shapeFilePaths, string table, string columns, Type objectType, object[] additionalColumnValues)
        {
            NpgsqlCommand cmd = DB.Connection.NewCommand("BEGIN");

            try
            {
                cmd.ExecuteNonQuery();

                foreach (string shapeFilePath in shapeFilePaths)
                {
                    string sridPath = Path.Combine(Path.GetDirectoryName(shapeFilePath), Path.GetFileNameWithoutExtension(shapeFilePath) + ".srid");
                    int fromSrid;
                    if (!File.Exists(sridPath) || !int.TryParse(File.ReadAllText(sridPath), out fromSrid))
                        throw new Exception("Could not find SRID file at \"" + sridPath + "\". Check that the file exists and contains the SRID for \"" + shapeFilePath + "\".");

                    string reprojection = Configuration.PostgisSRID.ToString();
                    if (fromSrid != Configuration.PostgisSRID)
                        reprojection = fromSrid + ":" + Configuration.PostgisSRID;

                    string shapeFileName = Path.GetFileNameWithoutExtension(shapeFilePath);

                    string sql;
                    string error;
                    using (Process process = new Process())
                    {
                        process.StartInfo.FileName = Configuration.Shp2PgsqlPath;
                        process.StartInfo.Arguments = "-I -g geom -s " + reprojection + " \"" + shapeFilePath + "\" temp";
                        process.StartInfo.CreateNoWindow = true;
                        process.StartInfo.UseShellExecute = false;
                        process.StartInfo.RedirectStandardError = true;
                        process.StartInfo.RedirectStandardOutput = true;
                        process.Start();

                        Console.Out.WriteLine("Converting shapefile \"" + shapeFilePath + "\".");

                        sql = process.StandardOutput.ReadToEnd().Replace("BEGIN;", "").Replace("COMMIT;", "");
                        error = process.StandardError.ReadToEnd().Trim().Replace(Environment.NewLine, "; ").Replace("\n", "; ");

                        process.WaitForExit();
                    }

                    Console.Out.WriteLine(error);

                    cmd.CommandText = sql;
                    cmd.ExecuteNonQuery();

                    Console.Out.WriteLine("Importing shapefile into database.");

                    MethodInfo create = objectType.GetMethod("Create", BindingFlags.NonPublic | BindingFlags.Static);
                    int shapeFileId = (int)create.Invoke(null, new object[] { cmd.Connection, shapeFileName }.Union(additionalColumnValues).ToArray());

                    ShapeFileGeometry.Create(cmd.Connection, shapeFileId, "temp", "geom");

                    cmd.CommandText = "DROP TABLE temp";
                    cmd.ExecuteNonQuery();
                }

                cmd.CommandText = "COMMIT";
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                throw new Exception("Failed to import shape files:  " + ex.Message);
            }
            finally
            {
                DB.Connection.Return(cmd.Connection);
            }
        }

        private int _id;
        private string _name;

        #region properties
        public int Id
        {
            get { return _id; }
        }

        public string Name
        {
            get { return _name; }
        }
        #endregion

        protected ShapeFile()
        {
        }

        protected virtual void Construct(NpgsqlDataReader reader)
        {
            _id = Convert.ToInt32(reader[Table + "_" + Columns.Id]);
            _name = Convert.ToString(reader[Table + "_" + Columns.Name]);
        }

        public virtual string Details()
        {
            return "ID:  " + _id + Environment.NewLine +
                   "Name:  " + _name + Environment.NewLine +
                   "Type:  " + GetType().Name;
        }

        public override string ToString()
        {
            return _name;
        }

        public void Delete()
        {
            DB.Connection.ExecuteNonQuery("DELETE FROM " + Table + " WHERE " + Columns.Id + "=" + _id);
        }
    }
}
