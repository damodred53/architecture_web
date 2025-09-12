namespace ApiElasticSearch.Objet;

public class ObjRechRap
{
    public string documents { get; set; }
    public List<MotObj> Mot { get; set; }
}
public class MotObj
{
    public string Mot { get; set; }
    public string Extrait { get; set; }
}