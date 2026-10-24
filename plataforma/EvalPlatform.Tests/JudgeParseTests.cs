using EvalPlatform.Services;

namespace EvalPlatform.Tests;

public class JudgeParseTests
{
    [Fact]
    public void Lee_el_formato_normal()
    {
        var (score, reason) = CustomJudges.ParseVerdict("<S0>comparo</S0><S1>Falta la ruta del menú.</S1><S2>3</S2>");
        Assert.Equal(3, score);
        Assert.Equal("Falta la ruta del menú.", reason);
    }

    [Fact]
    public void Lee_el_puntaje_cuando_el_modelo_copia_el_texto_de_ejemplo()
    {
        // Salida real de gpt-4.1 con la plantilla anterior: copió "<S0>tu razonamiento</S0>" y siguió sin cerrar las etiquetas.
        var texto = "<S0>tu razonamiento</S0>\nLa respuesta reconoce la causa del bloqueo pero omite la ruta.\n<S1>Falta la ruta de configuración.</S1>\n<S2> 3";
        var (score, reason) = CustomJudges.ParseVerdict(texto);
        Assert.Equal(3, score);
        Assert.Equal("Falta la ruta de configuración.", reason);
    }

    [Fact]
    public void Lee_el_puntaje_al_final_sin_etiquetas()
    {
        var (score, _) = CustomJudges.ParseVerdict("La respuesta es parcialmente correcta.\nPuntaje: 3/5");
        Assert.Equal(3, score);
    }

    [Fact]
    public void Sin_puntaje_devuelve_el_motivo_del_error()
    {
        var (score, reason) = CustomJudges.ParseVerdict("<S0>tu razonamiento</S0> no sé");
        Assert.Null(score);
        Assert.StartsWith("El juez no devolvió un puntaje válido", reason);
    }

    [Fact]
    public void Ignora_numeros_fuera_de_rango()
    {
        var (score, _) = CustomJudges.ParseVerdict("<S1>x</S1><S2>7</S2>");
        Assert.Null(score);
    }
}
