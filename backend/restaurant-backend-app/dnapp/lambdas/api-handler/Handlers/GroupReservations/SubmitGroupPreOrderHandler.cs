using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using APIHandler.Exceptions;
using APIHandler.Helpers;
using APIHandler.Models.GroupReservation;
using APIHandler.Services.GroupReservations;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace APIHandler.Handlers.GroupReservations;

public class SubmitGroupPreOrderHandler(IGroupReservationService service) : IApiRequestHandler
{
    public async Task<APIGatewayProxyResponse> HandleRequest(
        APIGatewayProxyRequest request,
        ILambdaContext context)
    {
        string? userId = UserContextHelper.GetAuthenticatedUserId(request);
        if (userId is null)
            return ResponseBuilder.CreateErrorResponse(401, "Unauthorized.", context.Logger);

        if (!request.TryGetPathValue("groupReservationId", out string? groupReservationId))
        {
            return ResponseBuilder.CreateErrorResponse(
                400,
                "Missing 'groupReservationId' in path parameters.",
                context.Logger);
        }

        if (!RequestBodyHelper.TryParseAndValidate(
                request,
                context,
                out SubmitGroupPreOrderRequest? requestBody,
                out APIGatewayProxyResponse? errorResponse))
        {
            return errorResponse;
        }

        try
        {
            IReadOnlyList<SubmitGroupPreOrderResponse> result = await service.SubmitGroupPreOrderAsync(
                userId,
                groupReservationId,
                requestBody.ReservationId);

            return ResponseBuilder.CreateJsonResponse(200, result);
        }
        catch (GroupReservationNotFoundException exception)
        {
            return ResponseBuilder.CreateErrorResponse(404, exception.Message, context.Logger);
        }
        catch (ReservationNotFoundException exception)
        {
            return ResponseBuilder.CreateErrorResponse(404, exception.Message, context.Logger);
        }
        catch (ActionForbiddenException exception)
        {
            return ResponseBuilder.CreateErrorResponse(403, exception.Message, context.Logger);
        }
        catch (GroupReservationExpiredException exception)
        {
            return ResponseBuilder.CreateErrorResponse(409, exception.Message, context.Logger);
        }
        catch (ValidationException exception)
        {
            return ResponseBuilder.CreateErrorResponse(400, exception.Message, context.Logger);
        }
    }
}
