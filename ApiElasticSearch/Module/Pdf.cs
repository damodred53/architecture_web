namespace ApiElasticSearch.Module;

public class Pdf
{
    public List<(string, bool)> ListeMot(string phrase)
    {
        List<(string, bool)> listeMot = new();

        foreach (var mot in phrase.Split(' '))
        {
            if (mot.Any(x => x != ' ') && mot != null)
            {
                listeMot.Add((mot, false));
            }
        }
        return listeMot;
    }
}