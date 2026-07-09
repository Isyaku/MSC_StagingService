using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Jaiz_POS_MSC_StagingService.Models
{
    public class MSC_RequestModel
    {
        [Key]
        public int Id { get; set; }
        public string? CompanyCode { get; set; }
        public string? BranchCode { get; set; }
        public string? Currency { get; set; }
        public string? GLCode { get; set; }
        public string? CIFNO { get; set; }
        public string? Serial { get; set; }
        public decimal? CVAmount { get; set; }
        public string? FCAmount { get; set; }
        public string? Rate { get; set; }
        public string? ValueDate { get; set; }
        public string? Description { get; set; }
        public string? TRXCode { get; set; }
        public string? JVType { get; set; }
        public string? TrateDate { get; set; }
        public string? AccountNumber { get; set; }
        public int? MSC_Request_Upload_ID { get; set; }
        public string? Status { get; set; }
        public string? DebitAcct { get; set; }
        public string? CreditAcct { get; set; }
        public string? RRNumber { get; set; }
        public string? TransCategory { get; set; }
        public string? TransactionId { get; set; }
        public string? SettlementDescription { get; set; }
    }
}
