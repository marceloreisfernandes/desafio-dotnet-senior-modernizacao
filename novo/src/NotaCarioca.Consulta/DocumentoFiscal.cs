namespace NotaCarioca.Consulta;

public static class DocumentoFiscal
{
    // Canonical ASCII strings retain leading zeroes. Normalization belongs at the edge.
    public static bool Valido(string documento)
    {
        if (documento.Length is not (11 or 14) || documento.Any(c => c is < '0' or > '9') ||
            documento.All(c => c == documento[0])) return false;
        if (documento.Length == 11)
        {
            var soma = 0;
            for (var i = 0; i < 9; i++) soma += (documento[i] - '0') * (10 - i);
            var primeiro = soma * 10 % 11 % 10;
            soma = 0;
            for (var i = 0; i < 10; i++) soma += (documento[i] - '0') * (11 - i);
            return primeiro == documento[9] - '0' && soma * 10 % 11 % 10 == documento[10] - '0';
        }
        int[] pesos = [6, 5, 4, 3, 2, 9, 8, 7, 6, 5, 4, 3, 2];
        var total = 0;
        for (var i = 0; i < 12; i++) total += (documento[i] - '0') * pesos[i + 1];
        var resto = total % 11;
        var digito = resto < 2 ? 0 : 11 - resto;
        if (digito != documento[12] - '0') return false;
        total = 0;
        for (var i = 0; i < 13; i++) total += (documento[i] - '0') * pesos[i];
        resto = total % 11;
        return (resto < 2 ? 0 : 11 - resto) == documento[13] - '0';
    }
}
