namespace FMSC_SMS.Core;

internal static partial class FeeBillDataBuilder
{
    internal static DataTable BuildGrouped(string institutionName,string contactNumber,string address,string parent,DataTable charges)
    {
        var data=FeeBillPresentation.Create();
        data.Columns.Add("FamilyRowKind",typeof(string));
        data.Columns.Add("FamilyAmountText",typeof(string));
        data.Columns.Add("BillStudentKey",typeof(string));
        data.Columns.Add("BillStudentName",typeof(string));
        FamilyFeeDocumentPresentation.AddChildColumns(data);
        decimal total=charges.AsEnumerable().Sum(r=>Convert.ToDecimal(r["Due"]));
        decimal discount=charges.AsEnumerable().Sum(r=>Convert.ToDecimal(r["DiscountAmount"]));
        string references=string.Join(", ",FeeStudentOrder.Rows(charges,"Reference").Select(r=>Convert.ToString(r["Reference"])).Distinct());
        bool multipleStudents=charges.AsEnumerable().Select(r=>Convert.ToString(r["Reference"])).Distinct().Count()>1;
        string studentKey="",studentHeading="",studentPlacement="";
        string campusHeading=FeeDocumentData.CampusHeading(charges);
        void AddLine(string description,decimal amount,string kind,string period="",DataRow? charge=null){
            var line=data.Rows.Add(1,institutionName,multipleStudents?"FAMILY FEE BILL":"STUDENT FEE BILL","PARENT COPY","",
                multipleStudents?parent:Convert.ToString(charges.Rows[0]["StudentName"]),references,multipleStudents?"":studentPlacement,
                DateTime.Now.ToString("dd-MM-yyyy hh:mm tt"),"",
                description,amount,discount,total,0m,"","",discount>0m,false,
                contactNumber,address,campusHeading,multipleStudents?"":parent,"");
            line["DocumentKind"]="Bill";line["FamilyRowKind"]=kind;line["FamilyAmountText"]=kind=="Fee"?amount.ToString("N0"):"";
            line["BillSourceKey"]=charges.Columns.Contains("BillingChargeId")
                ?string.Join(",",charges.AsEnumerable().Select(row=>Convert.ToString(row["BillingChargeId"])).OrderBy(value=>value)):"";
            line["BillStudentKey"]=studentKey;line["BillStudentName"]=studentHeading;line["FeePeriod"]=FeeDocumentData.FormatPeriod(period);
            if(charge is not null)
            {
                line["BillLineDiscount"]=charge["DiscountAmount"];
                line["BillGrossAmount"]=charges.Columns.Contains("GrossAmount") && !charge.IsNull("GrossAmount")
                    ?charge["GrossAmount"]:amount+Convert.ToDecimal(charge["DiscountAmount"]);
                line["ChildFatherName"]=parent;
                line["ChildClassSection"]=studentPlacement;
                if(charges.Columns.Contains("CampusName"))line["ChildCampusName"]=charge["CampusName"];
                if(charges.Columns.Contains("BillingChargeId"))line["BillChargeId"]=charge["BillingChargeId"];
                if(charges.Columns.Contains("AdmissionId"))line["BillAdmissionId"]=charge["AdmissionId"];
                if(charges.Columns.Contains("IsVisitBill")&&Convert.ToBoolean(charge["IsVisitBill"]))
                {
                    line["BillIsVisit"]=true;line["BillAccountGross"]=charge["AccountGrossAmount"];line["BillAccountDiscount"]=charge["AccountDiscountAmount"];
                    line["BillDisplayOverride"]=charge["BaseAmount"];
                }
            }
        }
        // Group by the reference, never by name: siblings can share a name.
        foreach(var student in FeeStudentOrder.Rows(charges,"Reference").GroupBy(r=>Convert.ToString(r["Reference"]))){
            studentKey=student.Key??"";studentHeading=$"{student.Key} · {student.First()["StudentName"]}";
            studentPlacement=string.Join(" / ",student.Select(PlacementText).Where(value=>value.Length>0).Distinct(StringComparer.OrdinalIgnoreCase));
            AddLine(studentHeading,0m,"Student");
            foreach(var month in student.GroupBy(r=>r["BillingMonth"] is DateTime date?new DateTime(date.Year,date.Month,1):new DateTime(1,1,1)).OrderBy(g=>g.Key)){
                AddLine("  "+(month.Key.Year==1?"One-time fees":month.Key.ToString("MMM yyyy")),0m,"Month");
                foreach(var fee in month.OrderBy(r=>charges.Columns.Contains("FeeDisplayOrder")?Convert.ToInt32(r["FeeDisplayOrder"]):0).ThenBy(r=>Convert.ToString(r["FeeTypeName"])))
                {
                    string name=FeeDocumentData.FeeName(Convert.ToString(fee["FeeTypeName"])??"");
                    bool visit=charges.Columns.Contains("IsVisitBill")&&Convert.ToBoolean(fee["IsVisitBill"]);
                    if(visit&&Convert.ToBoolean(fee["IsCarriedForward"])&&Convert.ToDecimal(fee["OpeningBalance"])>0m)name+=" - Due";
                    AddLine(name,Convert.ToDecimal(fee["Due"]),"Fee",month.Key.Year==1?"One-time fees":month.Key.ToString("MMM yyyy"),fee);
                    if(visit)
                        foreach(var (field,caption) in new[]{("RateAdjustment","Fee adjustment"),("DiscountReversed","Discount withdrawn"),("SettlementAdjustment","Payment adjustment")})
                        {
                            decimal adjustment=Convert.ToDecimal(fee[field]);if(adjustment==0m)continue;
                            AddLine(caption+" - "+FeeDocumentData.FeeName(Convert.ToString(fee["FeeTypeName"])??""),0m,"Fee",month.Key.Year==1?"One-time fees":month.Key.ToString("MMM yyyy"));
                            var extra=data.Rows[^1];extra["BillGrossAmount"]=0m;extra["BillLineDiscount"]=0m;extra["BillDisplayOverride"]=adjustment;
                        }
                }
            }
        }
        return data;
    }
    internal static string PlacementText(DataRow charge)
    {
        string Read(string column)=>charge.Table.Columns.Contains(column)&&!charge.IsNull(column)
            ?Convert.ToString(charge[column])?.Trim()??"":"";
        string className=Read("ClassName"),section=Read("SectionName");
        return className.Length==0?"":section.Length==0?className:$"{className} - {section}";
    }

}
