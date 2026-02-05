using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace AtaBank
{
    class Program
    {
        static async Task Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            Console.Title = "AtaBank International - Professional Digital Banking";

            BankingSystem bank = new BankingSystem();
            await bank.Initialize();
            await bank.Run();
        }
    }

    #region Models
    public class User
    {
        public int Id { get; set; }
        public string FirstName { get; set; }
        public string LastName { get; set; }
        public string NationalId { get; set; }
        public string PasswordHash { get; set; }
        public decimal Balance { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    public class Transaction
    {
        public int Id { get; set; }
        public int UserId { get; set; }
        public string Type { get; set; }
        public decimal Amount { get; set; }
        public string Description { get; set; }
        public decimal BalanceBefore { get; set; }
        public decimal BalanceAfter { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    public class ExchangeRate
    {
        public string Code { get; set; }
        public string Name { get; set; }
        public decimal BuyRate { get; set; }
        public decimal SellRate { get; set; }
        public string Symbol { get; set; }
    }
    #endregion

    #region Services
    public class DatabaseManager
    {
        private readonly string connectionString;
        private const string DB_FILE = "atabank.db";

        public DatabaseManager()
        {
            connectionString = $"Data Source={DB_FILE};Version=3;";
        }

        public void InitializeDatabase()
        {
            if (!File.Exists(DB_FILE)) SQLiteConnection.CreateFile(DB_FILE);

            using (var connection = new SQLiteConnection(connectionString))
            {
                connection.Open();
                string[] queries = {
                    @"CREATE TABLE IF NOT EXISTS Users (Id INTEGER PRIMARY KEY AUTOINCREMENT, FirstName TEXT, LastName TEXT, NationalId TEXT UNIQUE, PasswordHash TEXT, Balance REAL DEFAULT 0, CreatedAt TEXT)",
                    @"CREATE TABLE IF NOT EXISTS CurrencyBalances (UserId INTEGER, CurrencyCode TEXT, Amount REAL DEFAULT 0, PRIMARY KEY (UserId, CurrencyCode))",
                    @"CREATE TABLE IF NOT EXISTS Transactions (Id INTEGER PRIMARY KEY AUTOINCREMENT, UserId INTEGER, Type TEXT, Amount REAL, Description TEXT, BalanceBefore REAL, BalanceAfter REAL, CreatedAt TEXT)"
                };
                foreach (var q in queries) { using (var cmd = new SQLiteCommand(q, connection)) cmd.ExecuteNonQuery(); }
            }
        }

        public User GetUserForLogin(string nationalId, string firstName, string lastName)
        {
            using (var conn = new SQLiteConnection(connectionString))
            {
                conn.Open();
                var cmd = new SQLiteCommand("SELECT * FROM Users WHERE NationalId = @nid AND LOWER(FirstName) = LOWER(@fn) AND LOWER(LastName) = LOWER(@ln)", conn);
                cmd.Parameters.AddWithValue("@nid", nationalId);
                cmd.Parameters.AddWithValue("@fn", firstName.Trim());
                cmd.Parameters.AddWithValue("@ln", lastName.Trim());
                using (var rdr = cmd.ExecuteReader())
                {
                    if (rdr.Read()) return new User
                    {
                        Id = Convert.ToInt32(rdr["Id"]),
                        FirstName = rdr["FirstName"].ToString(),
                        LastName = rdr["LastName"].ToString(),
                        NationalId = rdr["NationalId"].ToString(),
                        PasswordHash = rdr["PasswordHash"].ToString(),
                        Balance = Convert.ToDecimal(rdr["Balance"]),
                        CreatedAt = DateTime.Parse(rdr["CreatedAt"].ToString())
                    };
                }
            }
            return null;
        }

        public bool CreateUser(User u)
        {
            using (var conn = new SQLiteConnection(connectionString))
            {
                conn.Open();
                var cmd = new SQLiteCommand(@"INSERT INTO Users (FirstName, LastName, NationalId, PasswordHash, Balance, CreatedAt) VALUES (@f, @l, @n, @p, @b, @c)", conn);
                cmd.Parameters.AddWithValue("@f", u.FirstName); cmd.Parameters.AddWithValue("@l", u.LastName);
                cmd.Parameters.AddWithValue("@n", u.NationalId); cmd.Parameters.AddWithValue("@p", u.PasswordHash);
                cmd.Parameters.AddWithValue("@b", u.Balance); cmd.Parameters.AddWithValue("@c", u.CreatedAt.ToString("o"));
                return cmd.ExecuteNonQuery() > 0;
            }
        }

        public void UpdateUserBalance(int id, decimal bal)
        {
            using (var conn = new SQLiteConnection(connectionString))
            {
                conn.Open();
                var cmd = new SQLiteCommand("UPDATE Users SET Balance = @b WHERE Id = @i", conn);
                cmd.Parameters.AddWithValue("@b", bal); cmd.Parameters.AddWithValue("@i", id);
                cmd.ExecuteNonQuery();
            }
        }

        public void UpdateCurrencyBalance(int userId, string code, decimal amount)
        {
            using (var conn = new SQLiteConnection(connectionString))
            {
                conn.Open();
                var cmd = new SQLiteCommand(@"INSERT OR REPLACE INTO CurrencyBalances (UserId, CurrencyCode, Amount) VALUES (@u, @c, @a)", conn);
                cmd.Parameters.AddWithValue("@u", userId); cmd.Parameters.AddWithValue("@c", code); cmd.Parameters.AddWithValue("@a", amount);
                cmd.ExecuteNonQuery();
            }
        }

        public Dictionary<string, decimal> GetUserCurrencies(int userId)
        {
            var dict = new Dictionary<string, decimal>();
            using (var conn = new SQLiteConnection(connectionString))
            {
                conn.Open();
                var cmd = new SQLiteCommand("SELECT CurrencyCode, Amount FROM CurrencyBalances WHERE UserId = @id", conn);
                cmd.Parameters.AddWithValue("@id", userId);
                using (var r = cmd.ExecuteReader()) { while (r.Read()) dict[r.GetString(0)] = r.GetDecimal(1); }
            }
            return dict;
        }

        public void AddTransaction(Transaction t)
        {
            using (var conn = new SQLiteConnection(connectionString))
            {
                conn.Open();
                var cmd = new SQLiteCommand(@"INSERT INTO Transactions (UserId, Type, Amount, Description, BalanceBefore, BalanceAfter, CreatedAt) VALUES (@u, @t, @a, @d, @bb, @ba, @c)", conn);
                cmd.Parameters.AddWithValue("@u", t.UserId); cmd.Parameters.AddWithValue("@t", t.Type);
                cmd.Parameters.AddWithValue("@a", t.Amount); cmd.Parameters.AddWithValue("@d", t.Description);
                cmd.Parameters.AddWithValue("@bb", t.BalanceBefore); cmd.Parameters.AddWithValue("@ba", t.BalanceAfter);
                cmd.Parameters.AddWithValue("@c", t.CreatedAt.ToString("o"));
                cmd.ExecuteNonQuery();
            }
        }
    }

    public class ExchangeRateService
    {
        private static readonly HttpClient http = new HttpClient();
        private Dictionary<string, ExchangeRate> rates = new Dictionary<string, ExchangeRate>();
        private DateTime lastUpdate = DateTime.MinValue;

        public async Task<Dictionary<string, ExchangeRate>> GetExchangeRates()
        {
            if ((DateTime.Now - lastUpdate).TotalMinutes >= 1 || rates.Count == 0) await UpdateRates();
            return rates;
        }

        private async Task UpdateRates()
        {
            try
            {
                string url = "https://api.exchangerate-api.com/v4/latest/TRY";
                var response = await http.GetStringAsync(url);
                using (JsonDocument doc = JsonDocument.Parse(response))
                {
                    var ratesEl = doc.RootElement.GetProperty("rates");
                    var info = new Dictionary<string, (string n, string s)> { { "USD", ("US Dollar", "$") }, { "EUR", ("Euro", "€") }, { "GBP", ("British Pound", "£") } };
                    rates.Clear();
                    foreach (var c in info)
                    {
                        if (ratesEl.TryGetProperty(c.Key, out var val))
                        {
                            decimal rate = 1m / val.GetDecimal();
                            rates[c.Key] = new ExchangeRate { Code = c.Key, Name = c.Value.n, Symbol = c.Value.s, BuyRate = Math.Round(rate * 0.985m, 4), SellRate = Math.Round(rate * 1.015m, 4) };
                        }
                    }
                    lastUpdate = DateTime.Now;
                }
            }
            catch { /* Fallback Logic */ }
        }
    }
    #endregion

    public class BankingSystem
    {
        private DatabaseManager db;
        private ExchangeRateService ex;
        private User user;

        public async Task Initialize()
        {
            db = new DatabaseManager();
            db.InitializeDatabase();
            ex = new ExchangeRateService();
        }

        public async Task Run()
        {
            while (true)
            {
                Console.Clear();
                PrintHeader("AtaBank International - Welcome");

                if (user == null)
                {
                    Console.WriteLine("1. Login to Account");
                    Console.WriteLine("2. Open New Account");
                    Console.WriteLine("0. Exit Application");
                    Console.Write("\nSelection: ");
                    var s = Console.ReadLine();
                    if (s == "1") await Login();
                    else if (s == "2") Register();
                    else if (s == "0") break;
                }
                else await MainMenu();
            }
        }

        private async Task Login()
        {
            Console.Write("National ID: "); string nid = Console.ReadLine();
            Console.Write("First Name: "); string fn = Console.ReadLine();
            Console.Write("Last Name: "); string ln = Console.ReadLine();
            Console.Write("Password: "); string p = HashPassword(Console.ReadLine());

            var u = db.GetUserForLogin(nid, fn, ln);
            if (u != null && u.PasswordHash == p) { user = u; Console.WriteLine("\nAccess Granted. Loading..."); }
            else Console.WriteLine("\nAccess Denied. Invalid Credentials.");
            await Task.Delay(1500);
        }

        private void Register()
        {
            Console.Write("First Name: "); string f = Console.ReadLine();
            Console.Write("Last Name: "); string l = Console.ReadLine();
            Console.Write("National ID: "); string n = Console.ReadLine();
            Console.Write("Password: "); string p = Console.ReadLine();
            db.CreateUser(new User { FirstName = f, LastName = l, NationalId = n, PasswordHash = HashPassword(p), CreatedAt = DateTime.Now });
            Console.WriteLine("\nAccount Created. You may now login."); Console.ReadKey();
        }

        private async Task MainMenu()
        {
            var rates = await ex.GetExchangeRates();
            var currencies = db.GetUserCurrencies(user.Id);

            Console.Clear();
            PrintHeader($"AtaBank - Dashboard");
            Console.WriteLine($"Welcome, {user.FirstName} {user.LastName}");
            Console.WriteLine("------------------------------------------");
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"Main Balance: {user.Balance:N2} TRY");

            foreach (var c in currencies)
            {
                if (c.Value > 0 && rates.ContainsKey(c.Key))
                    Console.WriteLine($"{c.Key} Balance: {c.Value:N4} ({c.Value * rates[c.Key].BuyRate:N2} TRY)");
            }
            Console.ResetColor();
            Console.WriteLine("------------------------------------------");
            Console.WriteLine("1. Deposit Cash | 2. Withdraw Cash | 3. Currency Exchange | 0. Logout");
            Console.Write("\nSelection: ");
            var s = Console.ReadLine();

            if (s == "1") Deposit();
            else if (s == "2") Withdraw();
            else if (s == "3") await Exchange(rates);
            else if (s == "0") user = null;
        }

        private void Deposit()
        {
            Console.Write("Amount (TRY): ");
            if (decimal.TryParse(Console.ReadLine(), out decimal a))
            {
                decimal old = user.Balance; user.Balance += a;
                db.UpdateUserBalance(user.Id, user.Balance);
                db.AddTransaction(new Transaction { UserId = user.Id, Type = "Deposit", Amount = a, BalanceBefore = old, BalanceAfter = user.Balance, CreatedAt = DateTime.Now });
                Console.WriteLine("Deposit Successful.");
            }
            Console.ReadKey();
        }

        private void Withdraw()
        {
            Console.Write("Amount to Withdraw (TRY): ");
            if (decimal.TryParse(Console.ReadLine(), out decimal a))
            {
                if (user.Balance >= a)
                {
                    decimal old = user.Balance; user.Balance -= a;
                    db.UpdateUserBalance(user.Id, user.Balance);
                    db.AddTransaction(new Transaction { UserId = user.Id, Type = "Withdrawal", Amount = a, BalanceBefore = old, BalanceAfter = user.Balance, CreatedAt = DateTime.Now });
                    Console.WriteLine("Withdrawal Successful.");
                }
                else Console.WriteLine("Insufficient Funds.");
            }
            Console.ReadKey();
        }

        private async Task Exchange(Dictionary<string, ExchangeRate> currentRates)
        {
            int i = 1; var list = currentRates.Values.ToList();
            foreach (var r in list) Console.WriteLine($"{i++}. Buy {r.Code} - Rate: {r.SellRate:N4} TRY");
            Console.Write("\nSelect Currency: ");
            if (int.TryParse(Console.ReadLine(), out int choice) && choice <= list.Count)
            {
                var sel = list[choice - 1];
                Console.Write($"Quantity ({sel.Code}): ");
                if (decimal.TryParse(Console.ReadLine(), out decimal amt))
                {
                    decimal cost = amt * sel.SellRate;
                    if (user.Balance >= cost)
                    {
                        user.Balance -= cost;
                        db.UpdateUserBalance(user.Id, user.Balance);
                        var bals = db.GetUserCurrencies(user.Id);
                        decimal current = bals.ContainsKey(sel.Code) ? bals[sel.Code] : 0;
                        db.UpdateCurrencyBalance(user.Id, sel.Code, current + amt);
                        Console.WriteLine("Transaction Successful.");
                    }
                    else Console.WriteLine("Insufficient TRY Balance.");
                }
            }
            await Task.Delay(2000);
        }

        private string HashPassword(string p)
        {
            using (var s = SHA256.Create()) return Convert.ToBase64String(s.ComputeHash(Encoding.UTF8.GetBytes(p)));
        }

        private void PrintHeader(string title)
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("==========================================");
            Console.WriteLine($"   {title.ToUpper()}");
            Console.WriteLine("==========================================");
            Console.ResetColor();
        }
    }
}