using FMSC_SMS.Core;

internal static partial class Program
{
    private static void CheckCertificateCollectorDialog()
    {
        using var owner=new Form {ClientSize=new Size(850,600)};owner.Show();
        string? Run(bool accept)
        {
            Exception? failure=null;using var timer=new System.Windows.Forms.Timer {Interval=100};
            timer.Tick+=(_,_)=>{
                timer.Stop();var dialog=Application.OpenForms.Cast<Form>().Single(f=>f.Text==UiText.Certificates.Collection);
                try
                {
                    IEnumerable<Control> Walk(Control c)=>c.Controls.Cast<Control>().SelectMany(child=>new[]{child}.Concat(Walk(child)));
                    var input=Walk(dialog).OfType<ModernTextBox>().Single();
                    var collect=Walk(dialog).OfType<ModernButton>().Single(b=>b.Text=="Collect");
                    var cancel=Walk(dialog).OfType<ModernButton>().Single(b=>b.Text=="Cancel");
                    Check(!collect.Enabled,"A blank collector name cannot confirm collection");
                    input.Text="    ";Check(!collect.Enabled,"Whitespace cannot confirm collection");
                    input.Text=new string('A',201);Check(!collect.Enabled&&input.MaxLength==200,"Collector names follow the database's 200-character limit");
                    input.Text="  Ayesha Khan  ";Check(collect.Enabled,"A named collector enables the explicit Collect action");
                    var inputBounds=dialog.RectangleToClient(input.RectangleToScreen(input.ClientRectangle));
                    var collectBounds=dialog.RectangleToClient(collect.RectangleToScreen(collect.ClientRectangle));
                    var cancelBounds=dialog.RectangleToClient(cancel.RectangleToScreen(cancel.ClientRectangle));
                    string folder=Path.GetFullPath("artifacts/certificate-collection");Directory.CreateDirectory(folder);
                    using(var capture=new Bitmap(dialog.Width,dialog.Height)){dialog.DrawToBitmap(capture,new Rectangle(Point.Empty,capture.Size));capture.Save(Path.Combine(folder,"collector-dialog.png"));}
                    Check(inputBounds.Left>=AppScale.Scale(18)&&dialog.ClientSize.Width-inputBounds.Right>=AppScale.Scale(18),"Collector input has balanced inset spacing");
                    Check(collectBounds.Top>inputBounds.Bottom&&collectBounds.Right<=dialog.ClientSize.Width-AppScale.Scale(18)
                        &&cancelBounds.Right<collectBounds.Left,"Cancel and Collect fit below the field in a clear order");
                    var actionGroup = Rectangle.FromLTRB(cancelBounds.Left, Math.Min(cancelBounds.Top, collectBounds.Top),
                        collectBounds.Right, Math.Max(cancelBounds.Bottom, collectBounds.Bottom));
                    Check(HorizontallyCentered(actionGroup, dialog.ClientRectangle),
                        "Cancel and Collect stay grouped, centered and visible inside the dialog");
                    foreach(var label in Walk(dialog).OfType<Label>())
                    {
                        var bounds=dialog.RectangleToClient(label.RectangleToScreen(label.ClientRectangle));
                        Check(bounds.Top>=0&&bounds.Bottom<=collectBounds.Top&&label.Height>=label.GetPreferredSize(new Size(label.Width,0)).Height,
                            $"Collector captions and lock notice are readable without overlapping actions: {label.Text}; actual {label.Size}; preferred {label.GetPreferredSize(new Size(label.Width,0))}; bounds {bounds}; actions {collectBounds}");
                    }
                    Check(Walk(dialog).OfType<Label>().Any(l=>l.Text.Contains("permanently locks")),"Collection explains the saved-copy lock before confirming");
                    if(accept)
                    {
                        using var bitmap=new Bitmap(dialog.Width,dialog.Height);dialog.DrawToBitmap(bitmap,new Rectangle(Point.Empty,bitmap.Size));bitmap.Save(Path.Combine(folder,"collector-dialog.png"));
                        collect.PerformClick();
                    }
                    else cancel.PerformClick();
                }
                catch(Exception ex){failure=ex;dialog.Close();}
            };
            timer.Start();var result=CertificateCollectionDialog.Show(owner);if(failure is not null)throw failure;return result;
        }
        Check(Run(true)=="Ayesha Khan","Collect returns the trimmed collector name");
        Check(Run(false) is null,"Cancel leaves the certificate uncollected");
    }
}
