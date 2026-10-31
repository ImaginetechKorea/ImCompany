using Microsoft.Data.Sqlite;

namespace ImOrder;

// Version 1 backups remain compatible: this additive column defaults to false.
public static class SchemaUpgrade
{
    public static void EnsureCustomerPriority(string database)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Pooling = false, ForeignKeys = true }.ToString());
        connection.Open(); using var transaction = connection.BeginTransaction(); using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "PRAGMA table_info(Customers)"; bool exists = false; using (var reader = command.ExecuteReader()) while (reader.Read()) if (reader.GetString(1) == "IsPreferred") exists = true;
        if (!exists) { command.CommandText = "ALTER TABLE Customers ADD COLUMN IsPreferred INTEGER NOT NULL DEFAULT 0"; command.ExecuteNonQuery(); }
        command.CommandText = "PRAGMA table_info(Payments)"; bool hasOrder = false; using (var reader = command.ExecuteReader()) while (reader.Read()) if (reader.GetString(1) == "OrderId") hasOrder = true;
        if (!hasOrder) { command.CommandText = "ALTER TABLE Payments ADD COLUMN OrderId INTEGER NULL REFERENCES Orders(Id)"; command.ExecuteNonQuery(); }
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS Assets (Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT, Version INTEGER NOT NULL, CreatedAtUtc TEXT NOT NULL, UpdatedAtUtc TEXT NOT NULL, Name TEXT NOT NULL, Category TEXT NOT NULL, Model TEXT NOT NULL, SerialNumber TEXT NOT NULL, PurchaseDate TEXT NULL, PurchaseAmount INTEGER NOT NULL, Location TEXT NOT NULL, Custodian TEXT NOT NULL, Status TEXT NOT NULL, OrderId INTEGER NULL, Memo TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS PurchaseOrders (Id INTEGER NOT NULL CONSTRAINT PK_PurchaseOrders PRIMARY KEY AUTOINCREMENT, Version INTEGER NOT NULL, CreatedAtUtc TEXT NOT NULL, UpdatedAtUtc TEXT NOT NULL, Number TEXT NOT NULL, SupplierId INTEGER NOT NULL, SalesOrderId INTEGER NULL, Title TEXT NOT NULL, Date TEXT NOT NULL, DueDate TEXT NULL, SupplyAmount INTEGER NOT NULL, VatAmount INTEGER NOT NULL, TotalAmount INTEGER NOT NULL, ItemsJson TEXT NOT NULL, PolicyJson TEXT NOT NULL, SupplierSnapshotJson TEXT NOT NULL, Memo TEXT NOT NULL, Status TEXT NOT NULL, FOREIGN KEY (SupplierId) REFERENCES Customers(Id) ON DELETE RESTRICT, FOREIGN KEY (SalesOrderId) REFERENCES Orders(Id) ON DELETE RESTRICT);
            CREATE UNIQUE INDEX IF NOT EXISTS IX_PurchaseOrders_Number ON PurchaseOrders(Number);
            CREATE TABLE IF NOT EXISTS PurchaseReceipts (Id INTEGER NOT NULL CONSTRAINT PK_PurchaseReceipts PRIMARY KEY AUTOINCREMENT, Version INTEGER NOT NULL, CreatedAtUtc TEXT NOT NULL, UpdatedAtUtc TEXT NOT NULL, PurchaseOrderId INTEGER NOT NULL, Date TEXT NOT NULL, ItemsJson TEXT NOT NULL, Memo TEXT NOT NULL, Status TEXT NOT NULL, FOREIGN KEY (PurchaseOrderId) REFERENCES PurchaseOrders(Id) ON DELETE RESTRICT);
            CREATE TABLE IF NOT EXISTS PurchaseBills (Id INTEGER NOT NULL CONSTRAINT PK_PurchaseBills PRIMARY KEY AUTOINCREMENT, Version INTEGER NOT NULL, CreatedAtUtc TEXT NOT NULL, UpdatedAtUtc TEXT NOT NULL, PurchaseOrderId INTEGER NOT NULL, Date TEXT NOT NULL, DueDate TEXT NULL, SupplyAmount INTEGER NOT NULL, VatAmount INTEGER NOT NULL, TotalAmount INTEGER NOT NULL, Reference TEXT NOT NULL, Memo TEXT NOT NULL, Status TEXT NOT NULL, FOREIGN KEY (PurchaseOrderId) REFERENCES PurchaseOrders(Id) ON DELETE RESTRICT);
            CREATE TABLE IF NOT EXISTS PurchaseDisbursements (Id INTEGER NOT NULL CONSTRAINT PK_PurchaseDisbursements PRIMARY KEY AUTOINCREMENT, Version INTEGER NOT NULL, CreatedAtUtc TEXT NOT NULL, UpdatedAtUtc TEXT NOT NULL, PurchaseOrderId INTEGER NOT NULL, Date TEXT NOT NULL, Amount INTEGER NOT NULL, Reference TEXT NOT NULL, Memo TEXT NOT NULL, Status TEXT NOT NULL, FOREIGN KEY (PurchaseOrderId) REFERENCES PurchaseOrders(Id) ON DELETE RESTRICT);
            CREATE INDEX IF NOT EXISTS IX_PurchaseReceipts_PurchaseOrderId ON PurchaseReceipts(PurchaseOrderId);
            CREATE INDEX IF NOT EXISTS IX_PurchaseBills_PurchaseOrderId ON PurchaseBills(PurchaseOrderId);
            CREATE INDEX IF NOT EXISTS IX_PurchaseDisbursements_PurchaseOrderId ON PurchaseDisbursements(PurchaseOrderId);
            """; command.ExecuteNonQuery();
        var assetColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase); command.CommandText = "PRAGMA table_info(Assets)";
        using (var reader = command.ExecuteReader()) while (reader.Read()) assetColumns.Add(reader.GetString(1));
        foreach (var (name, declaration) in new[] { ("DepreciationStartDate", "TEXT NULL"), ("UsefulLifeYears", "INTEGER NULL"), ("ResidualValue", "INTEGER NOT NULL DEFAULT 0"), ("DepreciationMethod", "TEXT NOT NULL DEFAULT 'None'"), ("ImmediateExpenseAmount", "INTEGER NULL"), ("DepreciationRateBps", "INTEGER NULL"), ("Vendor", "TEXT NOT NULL DEFAULT ''"), ("EvidenceType", "TEXT NOT NULL DEFAULT ''"), ("TotalPaid", "INTEGER NULL"), ("SupplyValue", "INTEGER NULL"), ("VatValue", "INTEGER NULL"), ("InputVatDeductible", "INTEGER NULL"), ("BusinessUseBps", "INTEGER NULL") })
            if (!assetColumns.Contains(name)) { command.CommandText = $"ALTER TABLE Assets ADD COLUMN {name} {declaration}"; command.ExecuteNonQuery(); }
        transaction.Commit();
    }
}
