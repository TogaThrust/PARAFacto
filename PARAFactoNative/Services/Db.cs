using Microsoft.Data.Sqlite;

namespace PARAFactoNative.Services;

public static class Db
{
    public static SqliteConnection Open()
    {
        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = AppPaths.DbPath,
            ForeignKeys = true,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString();

        var cn = new SqliteConnection(cs);
        cn.Open();
        return cn;
    }
}
