namespace blazor.tus.Execption;

public class InvalidHeaderException : Exception
{
    public InvalidHeaderException(string message) : base(message){}
}