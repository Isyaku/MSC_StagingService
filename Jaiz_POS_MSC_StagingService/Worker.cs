using CsvHelper;
using CsvHelper.Configuration;
using Jaiz_POS_MSC_StagingService.Models;
using Microsoft.Data.SqlClient;
using Oracle.ManagedDataAccess.Client;
using System.Data;
using System.Formats.Asn1;
using System.Globalization;

namespace Jaiz_POS_MSC_StagingService
{
    public class Worker : BackgroundService
    {
        private readonly ILogger<Worker> _logger;
        private readonly IConfiguration _configuration;

        public Worker(ILogger<Worker> logger, IConfiguration configuration)
        {
            _logger = logger;
            _configuration = configuration;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("MSC Staging Worker Started...");
            Console.WriteLine("MSC Staging Worker Started...");

            var interval = _configuration.GetValue<int>("ServiceSettings:PollingIntervalSeconds");

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    ProcessFilteredFiles();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Unhandled service error");
                    Console.WriteLine($"{ex}, Unhandled service error");
                }

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(interval), stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            _logger.LogInformation("Stagging Service Stopped");
            Console.WriteLine("Stagging Service Stopped");
        }

        public string GetSessionID()
        {
            string result = "";

            try
            {
                JaizInternalService.processmessageSoapClient client = new JaizInternalService.processmessageSoapClient(0);
                string res = client.getSessionID();
                result = "000006" + res;
                //return "000006";
            }

            catch (Exception ex)
            {
                _logger.LogError(ex, "Error Getting session Id");
                Console.WriteLine($"{ex}, Error Getting session Id");
            }
            return result;
        }

        public ImalDataModel GetAccountDetails(string account)

        {
            string _connectionString = _configuration.GetValue<string>("appConfiguration:imalConnString");

            ImalDataModel newAccounts = new ImalDataModel();

            try
            {
                string query = @"SELECT comp_code, branch_code, currency_code, gl_code, cif_sub_no, sl_no, account_number, additional_reference FROM imal.amf
                                 WHERE branch_code = (SELECT branch_code FROM imal.amf WHERE additional_reference = :account) AND gl_code = :glCode";

                using (var connection = new OracleConnection(_connectionString))
                using (var command = new OracleCommand(query, connection))
                {
                    command.Parameters.Add(":account", account);
                    command.Parameters.Add(":glCode", 508916);

                    connection.Open();

                    using (var reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            newAccounts.CompanyCode = reader["comp_code"].ToString();
                            newAccounts.BranchCode = reader["branch_code"].ToString();
                            newAccounts.Currency = reader["currency_code"].ToString();
                            newAccounts.GLCode = reader["gl_code"].ToString();
                            newAccounts.CIFNO = reader["cif_sub_no"].ToString();
                            newAccounts.Serial = reader["sl_no"].ToString();
                            newAccounts.AccountNumber = reader["account_number"].ToString();
                            newAccounts.AdditionalReference = reader["additional_reference"].ToString();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error Getting account details: {ex.Message}");
                Console.WriteLine($"Error Getting account details: {ex.Message}");
            }
            return newAccounts;
        }

        static void SafeMove(string source, string dest)
        {
            int retry = 3;

            while (retry > 0)
            {
                try
                {
                    File.Move(source, dest, true);
                    return;
                }
                catch (IOException)
                {
                    retry--;
                    Thread.Sleep(500);
                }
            }
        }

        void ProcessFilteredFiles()
        {
            // Folder paths
            string filteredFolder = _configuration.GetValue<string>("appConfiguration:Folders:Filtered");
            string processingFolder = _configuration.GetValue<string>("appConfiguration:Folders:Processing");
            string completedFolder = _configuration.GetValue<string>("appConfiguration:Folders:Completed");
            string failedFolder = _configuration.GetValue<string>("appConfiguration:Folders:Failed");

            // Connection string
            string connectionString = _configuration.GetValue<string>("ConnectionStrings:DefaultConnection");

            // Process filtered CSV files
            var files = Directory.GetFiles(filteredFolder, "*_filtered.csv");

            using var connection = new SqlConnection(connectionString);
            connection.Open();

            foreach (var file in files)
            {
                string processingFile = Path.Combine(processingFolder, Path.GetFileName(file));
                File.Move(file, processingFile, overwrite: true);


                int totalLines;
                using (var fs = new FileStream(processingFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var sr = new StreamReader(fs))
                {
                    totalLines = 0;
                    while (sr.ReadLine() != null)
                        totalLines++;
                }

                int processed = 0;

                var fileName = Path.GetFileNameWithoutExtension(file);
                var uploadId = int.Parse(fileName.Split('_')[0]);

                try
                {

                    _logger.LogInformation($"Processing {processingFile}");
                    Console.WriteLine($"Processing {processingFile}");
                    List<dynamic> records;

                    using (var reader = new StreamReader(processingFile))
                    using (var csv = new CsvReader(reader, new CsvConfiguration(CultureInfo.InvariantCulture)
                    {
                        PrepareHeaderForMatch = args => args.Header.Trim(),
                        MissingFieldFound = null,
                        HeaderValidated = null
                    }))
                    {
                        records = csv.GetRecords<dynamic>().ToList();
                    }

                    foreach (var record in records)
                    {
                        string GetValue(string key)
                        {
                            var val = ((IDictionary<string, object>)record).TryGetValue(key, out var v) ? v?.ToString() : "";
                            return val?.Trim() ?? "";
                        }

                        var merchantAccountNr = GetValue("Merchant_Account_Nr").PadLeft(10, '0');
                        var merchantId = GetValue("Merchant_ID");
                        var merchantNameLocation = GetValue("Merchant_Name_Location");
                        var settlementImpact = GetValue("Settlement_Impact");
                        var settlementImpactDesc = GetValue("Settlement_Impact_Desc");
                        var rrNumber = GetValue("Retrieval_Reference_Nr");
                        var trxnCategory = GetValue("trxn_category");

                        if (!decimal.TryParse(settlementImpact, NumberStyles.Any, CultureInfo.InvariantCulture, out var cvAmount))
                            cvAmount = 0;

                        var acctDetails = GetAccountDetails(merchantAccountNr);
                        var transientAcct = AccountModel.TransientAcct;
                        var sessionId = GetSessionID();

                        var model = new MSC_RequestModel
                        {
                            CompanyCode = acctDetails.CompanyCode,
                            BranchCode = acctDetails.BranchCode,
                            Currency = acctDetails.Currency,
                            GLCode = "508916",
                            CIFNO = "0",
                            Serial = acctDetails.Serial,
                            CVAmount = cvAmount,
                            ValueDate = DateTime.Now.ToString("dd/MM/yyyy"),
                            Description = $"{merchantId} | {merchantNameLocation}",
                            TRXCode = "1",
                            JVType = "1",
                            TrateDate = DateTime.Now.ToString("dd/MM/yyyy"),
                            AccountNumber = merchantAccountNr,
                            MSC_Request_Upload_ID = (int?)Convert.ToInt64(uploadId),
                            //DebitAcct = "{acctDetails.BranchCode.PadLeft(4, '0')}56650891600000000000",                         
                            DebitAcct = acctDetails.AdditionalReference,
                            CreditAcct = transientAcct,
                            Status = "2",
                            RRNumber = rrNumber,
                            TransCategory = trxnCategory,
                            TransactionId = sessionId,
                            SettlementDescription = settlementImpactDesc
                        };

                        using var cmd = new SqlCommand(@"
                            INSERT INTO MSC_Request
                            (
                                CompanyCode, BranchCode, Currency, GLCode, CIFNO, Serial, CVAmount, 
                                ValueDate, Description, TRXCode, JVType, TrateDate, AccountNumber, 
                                MSC_Request_Upload_ID, DebitAcct, CreditAcct, Status, RRNumber, TransCategory, 
                                TransactionId, SettlementDescription
                            )
                            VALUES
                            (
                                @CompanyCode, @BranchCode, @Currency, @GLCode, @CIFNO, @Serial, @CVAmount, 
                                @ValueDate, @Description, @TRXCode, @JVType, @TrateDate, @AccountNumber, 
                                @MSC_Request_Upload_ID, @DebitAcct, @CreditAcct, @Status, @RRNumber, @TransCategory, 
                                @TransactionId, @SettlementDescription
                            )", connection);

                        cmd.Parameters.AddWithValue("@CompanyCode", (object?)model.CompanyCode ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("@BranchCode", (object?)model.BranchCode ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("@Currency", (object?)model.Currency ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("@GLCode", (object?)model.GLCode ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("@CIFNO", (object?)model.CIFNO ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("@Serial", (object?)model.Serial ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("@CVAmount", model.CVAmount ?? 0);
                        cmd.Parameters.AddWithValue("@ValueDate", (object?)model.ValueDate ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("@Description", (object?)model.Description ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("@TRXCode", (object?)model.TRXCode ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("@JVType", (object?)model.JVType ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("@TrateDate", (object?)model.TrateDate ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("@AccountNumber", (object?)model.AccountNumber ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("@MSC_Request_Upload_ID", model.MSC_Request_Upload_ID ?? 0);
                        cmd.Parameters.AddWithValue("@DebitAcct", (object?)model.DebitAcct ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("@CreditAcct", (object?)model.CreditAcct ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("@Status", (object?)model.Status ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("@RRNumber", (object?)model.RRNumber ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("@TransCategory", (object?)model.TransCategory ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("@TransactionId", (object?)model.TransactionId ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("@SettlementDescription", (object?)model.SettlementDescription ?? DBNull.Value);

                        cmd.ExecuteNonQuery();

                        processed++;

                        if (processed % 1000 == 0)
                        {
                            if (totalLines > 0)
                            {
                                int current = (int)((double)processed / totalLines * 100);

                                using (SqlCommand progressCmd = new SqlCommand("UPDATE MSC_Request_Upload SET ProgressPercentage = @ProgressPercentage  WHERE Id = @Id", connection))
                                {
                                    progressCmd.Parameters.Add("@ProgressPercentage", SqlDbType.VarChar);
                                    progressCmd.Parameters.Add("@Id", SqlDbType.Int);

                                    progressCmd.Parameters["@ProgressPercentage"].Value = current;
                                    progressCmd.Parameters["@Id"].Value = uploadId;

                                    progressCmd.ExecuteNonQuery();
                                }
                            }
                        }
                    }


                    using (SqlCommand cmd = new SqlCommand(
                        @"UPDATE u SET u.Status = @Status, u.ProgressPercentage = @ProgressPercentage, u.TotalAmount = ISNULL((
                        SELECT SUM(CAST(r.CVAmount AS DECIMAL(18,2))) FROM MSC_Request r WHERE r.MSC_Request_Upload_ID = u.Id),
                        0) FROM MSC_Request_Upload u WHERE u.Id = @Id", connection))
                    {
                        cmd.Parameters.Add("@Status", SqlDbType.VarChar).Value = "0";
                        cmd.Parameters.Add("@ProgressPercentage", SqlDbType.Int).Value = 100;
                        cmd.Parameters.Add("@Id", SqlDbType.Int).Value = uploadId;

                        cmd.ExecuteNonQuery();
                    }
                    // Move processed file to completed
                    SafeMove(processingFile, Path.Combine(completedFolder, Path.GetFileName(processingFile)));

                    _logger.LogInformation($"Completed {processingFile}");
                    Console.WriteLine($"Completed {processingFile}");
                }
                catch (Exception ex)
                {
                    // Move file to failed
                    File.Move(processingFile, Path.Combine(failedFolder, Path.GetFileName(processingFile)), overwrite: true);

                    _logger.LogError($"Error processing {processingFile}: {ex.Message}");
                    Console.WriteLine($"Error processing {processingFile}: {ex.Message}");
                }
            }
            _logger.LogInformation("MSC Staging Worker Completed.");
            Console.WriteLine("MSC Staging Worker Completed.");
        }

    }
}
